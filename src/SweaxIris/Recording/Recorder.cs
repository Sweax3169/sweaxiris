using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using FFmpeg.AutoGen;
using SweaxIris.Audio;
using SweaxIris.Capture;
using SweaxIris.Core;
using SweaxIris.Encode;
using SweaxIris.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using BindFlags = Vortice.Direct3D11.BindFlags;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace SweaxIris.Recording;

public enum SourceKind { Screen, Window, Region }
public enum RecorderState { Ready, Recording, Paused, Finishing, Done, Failed }

/// <summary>What to record: a whole monitor, one window, or a rectangle of a monitor (in desktop pixels).</summary>
public sealed record CaptureTarget(SourceKind Kind, IntPtr Monitor, IntPtr Window, Win32.RECT Region)
{
    public static CaptureTarget Screen(IntPtr monitor) => new(SourceKind.Screen, monitor, IntPtr.Zero, default);
    public static CaptureTarget OfWindow(IntPtr hwnd) => new(SourceKind.Window, Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST), hwnd, default);

    public static CaptureTarget OfRegion(Win32.RECT r)
    {
        var mon = Win32.MonitorFromRect(ref r, Win32.MONITOR_DEFAULTTONEAREST);
        return new(SourceKind.Region, mon, IntPtr.Zero, r);
    }
}

public sealed class RecordingOptions
{
    public required CaptureTarget Target { get; init; }
    public required string OutputPath { get; init; }
    public bool Mkv { get; init; }
    public required EncoderSpec Encoder { get; init; }
    public int Fps { get; init; } = 60;
    /// <summary>Output height (0 = as captured); the width follows the shape.</summary>
    public int OutputHeight { get; init; }
    public bool ConstantQuality { get; init; } = true;
    public double Quality { get; init; } = 0.62;
    public int BitrateKbps { get; init; } = 20000;
    public int Speed { get; init; } = 1;
    public int KeyframeSeconds { get; init; } = 2;
    public bool Cursor { get; init; } = true;

    public bool SystemAudio { get; init; } = true;
    public string? SystemDevice { get; init; }
    public float SystemVolume { get; init; } = 1;
    /// <summary>When recording a window, take only that app's sound instead of everything the speakers play.</summary>
    public bool AppAudioOnly { get; init; }
    public bool Microphone { get; init; }
    public string? MicDevice { get; init; }
    public float MicVolume { get; init; } = 1;
    public bool MicMuted { get; init; }
    public bool SeparateTracks { get; init; }
    public int AudioBitrate { get; init; } = 192_000;
    /// <summary>Moves a device's sound earlier (negative) or later (100 ns), for devices that lag.</summary>
    public long SystemOffset { get; init; }
    public long MicOffset { get; init; }

    /// <summary>For tests: a picture source instead of the screen.</summary>
    public Func<Gpu, IVideoSource>? TestVideo { get; init; }
    /// <summary>For tests: extra audio sources (name → attach(input, timeline)).</summary>
    public List<(string Name, Func<MixerInput, Timeline, IDisposable> Attach)>? TestAudio { get; init; }
}

public sealed record RecordingResult(string Path, TimeSpan Duration, long Bytes, long Frames, long Dropped, long Late, string Encoder, int Width, int Height, int Fps, string? Error);

/// <summary>
/// One recording. <see cref="Prepare"/> opens the capture, the encoders and the file (do it during the
/// countdown); <see cref="Begin"/> starts the clock. Video is taken on a precise timer at the chosen frame
/// rate, sound is laid on the same clock, and pausing simply stops that clock: the file stays open and
/// continues seamlessly on resume.
/// </summary>
public sealed unsafe class Recorder : IDisposable
{
    readonly RecordingOptions _o;
    readonly Timeline _timeline = new();
    Gpu _gpu = null!;
    IVideoSource _source = null!;
    Converter _convert = null!;
    VideoEncoder _video = null!;
    Muxer _muxer = null!;
    Mixer? _mixer;
    readonly List<CaptureStream> _streams = [];
    readonly List<IDisposable> _testAudio = [];
    Thread? _tick, _encode;
    readonly BlockingCollection<Job> _queue = new(new ConcurrentQueue<Job>());
    ID3D11Texture2D? _black;

    // memory path (encoders that cannot read GPU textures)
    ID3D11Texture2D[]? _staging;
    int[]? _stagingBusy;

    long _frames, _dropped, _late;
    volatile bool _ending;
    long _endActive = -1;
    Exception? _failure;

    public RecorderState State { get; private set; } = RecorderState.Ready;
    public RecordingOptions Options => _o;
    public TimeSpan Elapsed => TimeSpan.FromTicks(_timeline.Elapsed);
    public long Frames => Interlocked.Read(ref _frames);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Late => Interlocked.Read(ref _late);
    public long Bytes => _muxer?.BytesWritten ?? 0;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public MixerInput? SystemInput { get; private set; }
    public MixerInput? MicInput { get; private set; }
    public string EncoderName => _o.Encoder.ToString();
    public Gpu Gpu => _gpu;
    public IVideoSource Source => _source;
    public event Action<string>? Failed;
    public event Action? SourceLost;

    /// <summary>Called on the capture clock, about 20 times a second, with the GPU held (for the live preview).</summary>
    public Action<Gpu, ID3D11Texture2D, int, int>? Preview;

    readonly struct Job(AVFrame* frame, int slot, long pts)
    {
        public readonly AVFrame* Frame = frame;
        public readonly int Slot = slot;
        public readonly long Pts = pts;
    }

    Recorder(RecordingOptions o) => _o = o;

    /// <summary>Opens everything so that <see cref="Begin"/> can start at once. Throws with a readable message on failure.</summary>
    public static Recorder Prepare(RecordingOptions o)
    {
        var r = new Recorder(o);
        try
        {
            r.Open();
            return r;
        }
        catch
        {
            r.Dispose();
            throw;
        }
    }

    void Open()
    {
        FF.Init();
        var t = _o.Target;
        var enc = _o.Encoder;
        // the encoder's card when it is a GPU encoder, else the card showing the picture
        uint vendor = enc.Vendor;
        AdapterInfo? screenCard = t.Monitor != IntPtr.Zero ? Capture.Gpu.AdapterForMonitor(t.Monitor) : null;
        _gpu = Capture.Gpu.Create(a => vendor != 0 ? a.VendorId == vendor : screenCard == null || a.Index == screenCard.Index);
        if (vendor != 0 && _gpu.Adapter.VendorId != vendor)
            throw new InvalidOperationException($"{enc.BackendName} needs a {(vendor == AdapterInfo.Nvidia ? "NVIDIA" : vendor == AdapterInfo.Amd ? "AMD" : "Intel")} graphics card.");

        if (_o.TestVideo != null) _source = _o.TestVideo(_gpu);
        else if (t.Kind == SourceKind.Window)
        {
            if (!Win32.IsWindow(t.Window)) throw new InvalidOperationException("That window is gone.");
            _source = WgcSource.ForWindow(_gpu, t.Window, _o.Cursor);
        }
        else
        {
            Win32.RECT? crop = null;
            if (t.Kind == SourceKind.Region)
            {
                var mon = Win32.MonitorInfo(t.Monitor).rcMonitor;
                crop = new Win32.RECT { Left = t.Region.Left - mon.Left, Top = t.Region.Top - mon.Top, Right = t.Region.Right - mon.Left, Bottom = t.Region.Bottom - mon.Top };
            }
            _source = WgcSource.ForMonitor(_gpu, t.Monitor, crop, _o.Cursor);
        }
        _source.Lost += () => SourceLost?.Invoke();

        // the recording size: even numbers, and the chosen height if smaller than the source
        int sw = _source.Width, sh = _source.Height;
        double scale = _o.OutputHeight > 0 && _o.OutputHeight < sh ? (double)_o.OutputHeight / sh : 1;
        Width = Math.Max(16, (int)Math.Round(sw * scale / 2) * 2);
        Height = Math.Max(16, (int)Math.Round(sh * scale / 2) * 2);

        Directory.CreateDirectory(Path.GetDirectoryName(_o.OutputPath)!);
        _muxer = new Muxer(_o.OutputPath, _o.Mkv);
        var vp = new VideoParams(Width, Height, _o.Fps, _o.ConstantQuality, _o.Quality, _o.BitrateKbps, _o.Speed, _o.KeyframeSeconds);
        _video = new VideoEncoder(_gpu, enc, vp, _muxer.NeedsGlobalHeader);
        _muxer.AddVideo(_video);
        _convert = new Converter(_gpu, Width, Height);
        if (!_video.UsesGpuFrames)
        {
            _staging = new ID3D11Texture2D[6];
            _stagingBusy = new int[_staging.Length];
            for (int i = 0; i < _staging.Length; i++)
                _staging[i] = _gpu.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
                });
        }
        _black = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = 16, Height = 16, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        });
        using (_gpu.Enter())
        {
            using var rtv = _gpu.Device.CreateRenderTargetView(_black);
            _gpu.Context.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0, 0, 0, 1));
            _convert.Run(_black, 16, 16);
        }

        OpenAudio();
        _muxer.Start();
    }

    void OpenAudio()
    {
        var inputs = new List<MixerInput>();
        MixerInput? sys = null, mic = null;
        var t = _o.Target;
        if (_o.SystemAudio)
        {
            sys = new MixerInput("System") { Volume = _o.SystemVolume };
            CaptureStream s;
            if (_o.AppAudioOnly && t.Kind == SourceKind.Window && t.Window != IntPtr.Zero)
            {
                Win32.GetWindowThreadProcessId(t.Window, out uint pid);
                s = new CaptureStream(CaptureStream.Kind.App, processId: pid);
            }
            else s = new CaptureStream(CaptureStream.Kind.System, _o.SystemDevice);
            Attach(s, sys, _o.SystemOffset);
            inputs.Add(sys);
        }
        if (_o.Microphone)
        {
            mic = new MixerInput("Microphone") { Volume = _o.MicVolume, Muted = _o.MicMuted };
            Attach(new CaptureStream(CaptureStream.Kind.Microphone, _o.MicDevice), mic, _o.MicOffset);
            inputs.Add(mic);
        }
        if (_o.TestAudio != null)
            foreach (var (name, attach) in _o.TestAudio)
            {
                var input = new MixerInput(name);
                _testAudio.Add(attach(input, _timeline));
                inputs.Add(input);
                sys ??= input;
            }
        SystemInput = sys;
        MicInput = mic;
        if (inputs.Count == 0) return;

        bool gh = _muxer.NeedsGlobalHeader;
        var tracks = new List<MixerTrack>();
        var mix = new AudioEncoder(_o.AudioBitrate, gh);
        tracks.Add(new MixerTrack("Mix", mix, _muxer.AddStream(mix.Context, "SweaxIris mix", true), inputs.ToArray()));
        if (_o.SeparateTracks && inputs.Count > 1)
        {
            foreach (var input in inputs)
            {
                var e = new AudioEncoder(_o.AudioBitrate, gh);
                tracks.Add(new MixerTrack(input.Name, e, _muxer.AddStream(e.Context, input.Name, false), [input]));
            }
        }
        _mixer = new Mixer(_timeline, tracks.ToArray(), _muxer);
        _tracks = tracks;
    }

    List<MixerTrack> _tracks = [];

    void Attach(CaptureStream s, MixerInput input, long offset)
    {
        s.Packet += (data, frames, time, silent) =>
        {
            long act = _timeline.Active(time + offset);
            if (act < 0) return;
            input.Push(data, frames, Mixer.ToSamples(act));
        };
        _streams.Add(s);
        s.Start();
    }

    /// <summary>Starts the clock: from now on everything is recorded.</summary>
    public void Begin()
    {
        if (State != RecorderState.Ready) return;
        _timeline.Begin(Clock.Now);
        State = RecorderState.Recording;
        _mixer?.Start();
        _encode = new Thread(EncodeLoop) { IsBackground = true, Name = "SweaxIris.Encode", Priority = ThreadPriority.AboveNormal };
        _encode.Start();
        _tick = new Thread(TickLoop) { IsBackground = true, Name = "SweaxIris.Clock", Priority = ThreadPriority.Highest };
        _tick.Start();
        Paths.Log($"recording {Width}x{Height}@{_o.Fps} {_o.Encoder} → {_o.OutputPath}");
    }

    public void Pause()
    {
        if (State != RecorderState.Recording) return;
        _timeline.Pause(Clock.Now);
        State = RecorderState.Paused;
    }

    public void Resume()
    {
        if (State != RecorderState.Paused) return;
        _timeline.Resume(Clock.Now);
        State = RecorderState.Recording;
    }

    public void SetMicMuted(bool muted) { if (MicInput != null) MicInput.Muted = muted; }
    public void SetVolumes(float system, float mic)
    {
        if (SystemInput != null) SystemInput.Volume = system;
        if (MicInput != null) MicInput.Volume = mic;
    }

    // ── video clock ─────────────────────────────────────────────────────────

    void TickLoop()
    {
        uint task = 0;
        IntPtr mm = Win32.AvSetMmThreadCharacteristicsW("Capture", ref task);
        Win32.timeBeginPeriod(1);
        IntPtr timer = Win32.CreateWaitableTimerExW(IntPtr.Zero, null, Win32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Win32.TIMER_ALL_ACCESS);
        int fps = _o.Fps;
        long last = -1;
        int previewEvery = Math.Max(1, fps / 20), sincePreview = 0;
        try
        {
            while (true)
            {
                // the clock runs a little behind real time, so the frame composed at each tick's moment has
                // already arrived; sound is placed by its own timestamps, so both stay together
                long lag = Math.Clamp(_source.DeliveryLatency + 20_000, 20_000, 400_000);
                long now = Clock.Now - lag;
                long act = _timeline.Active(now);
                if (act < 0)
                {
                    if (_ending)
                    {
                        // fill up to the moment Stop was pressed so the picture lasts as long as the sound
                        long endIdx = Interlocked.Read(ref _endActive) * fps / 10_000_000;
                        for (long k = last + 1; k < endIdx && k <= last + 4; k++) Emit(k, fresh: k == last + 1, now);
                        break;
                    }
                    Sleep(timer, 20_000); // paused
                    continue;
                }
                // the frame whose moment is nearest; the first one is always frame 0 so audio and video start together
                long idx = last < 0 ? 0 : (act * fps + 5_000_000) / 10_000_000;
                if (idx > last)
                {
                    long gap = idx - last - 1;
                    // a hiccup of a few frames: repeat the last picture so the file keeps a steady frame rate
                    if (gap is > 0 and <= 4)
                    {
                        for (long k = 1; k <= gap; k++) Emit(last + k, fresh: false, now);
                        Interlocked.Add(ref _late, gap);
                    }
                    else if (gap > 0) Interlocked.Add(ref _dropped, gap);
                    bool preview = Preview != null && ++sincePreview >= previewEvery;
                    if (preview) sincePreview = 0;
                    Emit(idx, fresh: true, now, preview);
                    last = idx;
                }
                // wake exactly at the next frame's moment
                long nextAct = ((last + 1) * 10_000_000 + fps - 1) / fps;
                long wait = nextAct - _timeline.Active(Clock.Now - lag);
                if (wait > 0) Sleep(timer, Math.Min(wait, 200_000));
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            Win32.CloseHandle(timer);
            Win32.timeEndPeriod(1);
            if (mm != IntPtr.Zero) Win32.AvRevertMmThreadCharacteristics(mm);
            _queue.CompleteAdding();
        }
    }

    static void Sleep(IntPtr timer, long hundredNs)
    {
        long due = -hundredNs;
        if (timer != IntPtr.Zero && Win32.SetWaitableTimerEx(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            Win32.WaitForSingleObject(timer, 1000);
        else Thread.Sleep((int)Math.Max(1, hundredNs / 10_000));
    }

    void Emit(long pts, bool fresh, long now, bool preview = false)
    {
        if (_video.UsesGpuFrames)
        {
            var f = _video.NewGpuFrame(out var tex, out uint slice);
            if (f == null) { Interlocked.Increment(ref _dropped); return; }
            using (_gpu.Enter())
            {
                if (fresh) Convert(now, preview);
                _gpu.Context.CopySubresourceRegion(tex, slice, 0, 0, 0, _convert.Output, 0);
                _gpu.Context.Flush();
            }
            f->pts = pts;
            _queue.Add(new Job(f, -1, pts));
        }
        else
        {
            int slot = -1;
            for (int i = 0; i < _stagingBusy!.Length && slot < 0; i++) if (Volatile.Read(ref _stagingBusy[i]) == 0) slot = i;
            if (slot < 0) { Interlocked.Increment(ref _dropped); return; }
            Volatile.Write(ref _stagingBusy![slot], 1);
            using (_gpu.Enter())
            {
                if (fresh) Convert(now, preview);
                _gpu.Context.CopyResource(_staging![slot], _convert.Output);
                _gpu.Context.Flush();
            }
            _queue.Add(new Job(null, slot, pts));
        }
        Interlocked.Increment(ref _frames);
    }

    void Convert(long now, bool preview)
    {
        _source.Prepare(now);
        var latest = _source.Latest;
        if (latest != null)
        {
            _convert.Run(latest, _source.Width, _source.Height);
            if (preview) Preview?.Invoke(_gpu, latest, _source.Width, _source.Height);
        }
    }

    // ── encoder thread ──────────────────────────────────────────────────────

    void EncodeLoop()
    {
        try
        {
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                AVFrame* f = job.Frame;
                if (f == null)
                {
                    f = _video.NewMemoryFrame();
                    CopyFromStaging(job.Slot, f);
                    Volatile.Write(ref _stagingBusy![job.Slot], 0);
                    f->pts = job.Pts;
                }
                try { _video.Encode(f, p => _muxer.Write(p, 0)); }
                finally { ffmpeg.av_frame_free(&f); }
            }
            _video.Encode(null, p => _muxer.Write(p, 0));
        }
        catch (Exception ex)
        {
            Fail(ex);
            // keep draining so the clock never blocks
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                var f = job.Frame;
                if (f != null) ffmpeg.av_frame_free(&f);
                else Volatile.Write(ref _stagingBusy![job.Slot], 0);
            }
        }
    }

    void CopyFromStaging(int slot, AVFrame* f)
    {
        var tex = _staging![slot];
        MappedSubresource map;
        // don't hold the device while the GPU finishes: poll without waiting
        while (true)
        {
            using (_gpu.Enter())
            {
                var hr = _gpu.Context.Map(tex, 0, MapMode.Read, MapFlags.DoNotWait, out map);
                if (hr.Success) break;
                if (hr.Code != unchecked((int)0x887A000A)) hr.CheckError(); // DXGI_ERROR_WAS_STILL_DRAWING
            }
            Thread.Sleep(0);
        }
        try
        {
            byte* src = (byte*)map.DataPointer;
            int pitch = (int)map.RowPitch;
            for (int y = 0; y < Height; y++)
                Buffer.MemoryCopy(src + (long)y * pitch, f->data[0] + (long)y * f->linesize[0], f->linesize[0], Width);
            byte* uv = src + (long)pitch * Height;
            for (int y = 0; y < Height / 2; y++)
                Buffer.MemoryCopy(uv + (long)y * pitch, f->data[1] + (long)y * f->linesize[1], f->linesize[1], Width);
        }
        finally
        {
            using (_gpu.Enter()) _gpu.Context.Unmap(tex, 0);
        }
    }

    void Fail(Exception ex)
    {
        if (_failure != null) return;
        _failure = ex;
        Paths.Log("recorder: " + ex);
        Failed?.Invoke(ex.Message);
    }

    // ── stopping ────────────────────────────────────────────────────────────

    /// <summary>Stops, finishes the file and returns what was recorded.</summary>
    public RecordingResult Stop()
    {
        var sw = Stopwatch.StartNew();
        if (State is RecorderState.Recording or RecorderState.Paused)
        {
            State = RecorderState.Finishing;
            _endActive = _timeline.End(Clock.Now);
            _ending = true;
            _tick?.Join(5000);
            _mixer?.Finish(_endActive);
            _encode?.Join(15000);
        }
        string? error = _failure?.Message ?? _mixer?.Failure?.Message;
        try { _muxer.Finish(); }
        catch (Exception ex) { error ??= ex.Message; }
        foreach (var s in _streams) s.Dispose();
        _streams.Clear();
        long bytes = File.Exists(_o.OutputPath) ? new FileInfo(_o.OutputPath).Length : 0;
        State = error == null ? RecorderState.Done : RecorderState.Failed;
        var result = new RecordingResult(_o.OutputPath, TimeSpan.FromTicks(Math.Max(0, _endActive)), bytes, Frames, Dropped, Late,
            _o.Encoder.ToString(), Width, Height, _o.Fps, error);
        Paths.Log($"stopped in {sw.ElapsedMilliseconds} ms: {result}");
        return result;
    }

    public void Dispose()
    {
        _ending = true;
        if (_timeline.Started) _timeline.End(Clock.Now);
        if (_tick?.IsAlive == true) _tick.Join(3000);
        if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
        if (_encode?.IsAlive == true) _encode.Join(5000);
        _mixer?.Dispose();
        foreach (var s in _streams) s.Dispose();
        foreach (var t in _testAudio) t.Dispose();
        foreach (var t in _tracks) t.Encoder.Dispose();
        _source?.Dispose();
        _video?.Dispose();
        _muxer?.Dispose();
        _convert?.Dispose();
        _black?.Dispose();
        if (_staging != null) foreach (var s in _staging) s.Dispose();
        _gpu?.Dispose();
    }
}
