using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SweaxIris.Capture;
using SweaxIris.Core;
using SweaxIris.Encode;
using SweaxIris.Native;

namespace SweaxIris.Recording;

public enum StudioState { Idle, Arming, Recording, Paused, Finishing }

/// <summary>A monitor as the UI shows it.</summary>
/// <param name="Number">The number Windows gives the display in Settings → Display (1, 2, 3…).</param>
public sealed record MonitorItem(IntPtr Handle, string Device, string Name, Win32.RECT Bounds, bool Primary, double Hz, int Number)
{
    /// <summary>"Display 2 · A27 V2": the number tells apart two monitors of the same model.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? Loc.F("mon.only", Number) : Loc.F("mon.title", Number, Name);
}

/// <summary>A finished recording in the Takes shelf.</summary>
public sealed record Take(string Path, DateTime When, double Seconds, long Bytes, int Width, int Height, int Fps, string Encoder, string? Thumb, long Dropped);

/// <summary>
/// The conductor between the windows and the recorder: works out what to record from the settings, runs the
/// countdown while the encoder opens, and handles pause, mic mute, stop and the list of takes. UI thread only.
/// </summary>
public sealed class Studio
{
    readonly Dispatcher _ui;
    public Settings S { get; }
    public StudioState State { get; private set; } = StudioState.Idle;
    public Recorder? Recorder { get; private set; }
    public CaptureTarget? Target { get; private set; }
    public IntPtr ChosenWindow { get; set; }
    public bool MicMuted { get; private set; }
    public List<Take> Takes { get; } = [];

    /// <summary>Probe results: encoder key → null when it works, else why not. Null until the probe finishes.</summary>
    public Dictionary<string, string?>? Encoders { get; private set; }
    public event Action? EncodersReady;
    public event Action? StateChanged;
    public event Action<int>? CountdownTick;
    public event Action<Take>? Saved;
    public event Action<string>? Error;
    /// <summary>Small BGRA frames of whatever is being recorded (raised on the UI thread).</summary>
    public event Action<BitmapSource>? RecordingPreview;

    // ── demo hooks: the README pictures drive the windows with made-up state, never the real screen ──
    public static bool Demo { get; set; }
    public TimeSpan? DemoElapsed { get; set; }
    public long? DemoBytes { get; set; }
    public Func<float>? DemoSystemPeak { get; set; }
    public Func<float>? DemoMicPeak { get; set; }
    public static List<MonitorItem>? DemoMonitors { get; set; }
    public static List<(string Title, string App)>? DemoWindows { get; set; }
    public void DemoSet(StudioState s) { State = s; StateChanged?.Invoke(); }
    public void DemoEncoders() => Encoders = EncoderSpec.All.ToDictionary(e => e.Key, e => e.Backend is Backend.Nvenc or Backend.Amf or Backend.X264 && !(e.Backend == Backend.Amf && e.Codec == Codec.Av1) ? null : (string?)"demo");
    public void DemoSet(CaptureTarget t) => Target = t;
    public void DemoSaved(Take t) { Takes.Insert(0, t); Saved?.Invoke(t); }

    public TimeSpan Elapsed => DemoElapsed ?? Recorder?.Elapsed ?? TimeSpan.Zero;
    public long Bytes => DemoBytes ?? Recorder?.Bytes ?? 0;
    public float SystemPeak => DemoSystemPeak?.Invoke() ?? Recorder?.SystemInput?.Peak ?? -1;
    public float MicPeak => DemoMicPeak?.Invoke() ?? Recorder?.MicInput?.Peak ?? -1;

    PreviewPump? _pump;
    byte[]? _lastFrame;
    int _lastW, _lastH;
    CancellationTokenSource? _arming;

    public Studio(Settings s, Dispatcher ui)
    {
        S = s;
        _ui = ui;
        LoadTakes();
    }

    // ── encoders ────────────────────────────────────────────────────────────

    public void ProbeEncoders()
    {
        Task.Run(() =>
        {
            var gpus = new Dictionary<uint, Gpu?>();
            Gpu? For(uint vendor)
            {
                if (gpus.TryGetValue(vendor, out var g)) return g;
                try { g = vendor == 0 ? Gpu.Create() : Gpu.Adapters().Any(a => a.VendorId == vendor) ? Gpu.Create(a => a.VendorId == vendor) : null; }
                catch { g = null; }
                return gpus[vendor] = g;
            }
            var result = new Dictionary<string, string?>();
            foreach (var e in EncoderSpec.All) result[e.Key] = VideoEncoder.Probe(e, For);
            foreach (var g in gpus.Values) g?.Dispose();
            Paths.Log("encoders: " + string.Join(", ", result.Where(r => r.Value == null).Select(r => r.Key)));
            _ui.BeginInvoke(() => { Encoders = result; EncodersReady?.Invoke(); });
        });
    }

    public bool Works(EncoderSpec e) => Encoders == null || (Encoders.TryGetValue(e.Key, out var why) && why == null);

    /// <summary>The encoder to use: the chosen one if it works here, else the best for the card showing the picture.</summary>
    public EncoderSpec PickEncoder(CaptureTarget? t = null)
    {
        if (S.Encoder != "auto" && EncoderSpec.FromKey(S.Encoder) is { } chosen && Works(chosen)) return chosen;
        uint vendor = 0;
        var mon = t?.Monitor ?? IntPtr.Zero;
        if (mon != IntPtr.Zero && Gpu.AdapterForMonitor(mon) is { } card) vendor = card.VendorId;
        var order = new List<string>();
        if (vendor == AdapterInfo.Nvidia) order.Add("nvenc-h264");
        if (vendor == AdapterInfo.Amd) order.Add("amf-h264");
        if (vendor == AdapterInfo.Intel) order.Add("qsv-h264");
        order.AddRange(["nvenc-h264", "amf-h264", "qsv-h264", "x264-h264"]);
        foreach (var k in order)
            if (EncoderSpec.FromKey(k) is { } e && Encoders != null && Encoders.TryGetValue(k, out var why) && why == null) return e;
        return EncoderSpec.FromKey(vendor == AdapterInfo.Nvidia ? "nvenc-h264" : "x264-h264")!;
    }

    // ── what to record ──────────────────────────────────────────────────────

    public static List<MonitorItem> Monitors()
    {
        if (DemoMonitors != null) return DemoMonitors;
        var names = Win32.MonitorNames();
        var mons = Win32.MonitorList().Select(m => (m.Handle, m.Bounds, m.Primary, Device: Win32.MonitorDevice(m.Handle))).ToList();
        // Windows numbers displays by their \\.\DISPLAYn name, not by where they stand: the left one can be 2
        var order = mons.Select(m => m.Device).OrderBy(DisplayIndex).ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        var list = new List<MonitorItem>();
        foreach (var m in mons)
        {
            var (name, hz) = names.TryGetValue(m.Device, out var v) ? v : ("", 0);
            list.Add(new MonitorItem(m.Handle, m.Device, name, m.Bounds, m.Primary, hz, order.IndexOf(m.Device) + 1));
        }
        return list.OrderBy(m => m.Number).ToList();
    }

    static int DisplayIndex(string device)
    {
        int i = device.Length;
        while (i > 0 && char.IsDigit(device[i - 1])) i--;
        return int.TryParse(device[i..], out int n) ? n : int.MaxValue;
    }

    public MonitorItem? ChosenMonitor()
    {
        var all = Monitors();
        if (!string.IsNullOrEmpty(S.Monitor) && all.FirstOrDefault(m => m.Device.Equals(S.Monitor, StringComparison.OrdinalIgnoreCase)) is { } m) return m;
        Win32.GetCursorPos(out var p);
        var under = Win32.MonitorFromPoint(p, Win32.MONITOR_DEFAULTTONEAREST);
        return all.FirstOrDefault(x => x.Handle == under) ?? all.FirstOrDefault();
    }

    public Win32.RECT? LastRegion => S.LastRegion is { Length: 4 } r ? new Win32.RECT { Left = r[0], Top = r[1], Right = r[2], Bottom = r[3] } : null;

    /// <summary>The source the settings describe right now, or null when one is missing (no window/region yet).</summary>
    public CaptureTarget? CurrentTarget()
    {
        switch (S.Source)
        {
            case "window":
                return ChosenWindow != IntPtr.Zero && Win32.IsWindow(ChosenWindow) ? CaptureTarget.OfWindow(ChosenWindow) : null;
            case "region":
                return LastRegion is { } r ? CaptureTarget.OfRegion(r) : null;
            default:
                return ChosenMonitor() is { } m ? CaptureTarget.Screen(m.Handle) : null;
        }
    }

    public void SetRegion(Win32.RECT r)
    {
        S.LastRegion = [r.Left, r.Top, r.Right, r.Bottom];
        S.Source = "region";
        S.Save();
    }

    RecordingOptions Options(CaptureTarget t) => new()
    {
        Target = t,
        OutputPath = S.NewFilePath(t.Kind switch { SourceKind.Window => "window", SourceKind.Region => "region", _ => "screen" }),
        Mkv = S.Container == "mkv",
        Encoder = PickEncoder(t),
        Fps = S.Fps,
        OutputHeight = S.Resolution,
        ConstantQuality = S.ConstantQuality,
        Quality = S.Quality,
        BitrateKbps = S.BitrateKbps,
        Speed = S.Speed,
        KeyframeSeconds = S.KeyframeSeconds,
        Cursor = S.Cursor,
        SystemAudio = S.SystemAudio,
        SystemDevice = S.SystemDevice,
        SystemVolume = (float)S.SystemVolume,
        AppAudioOnly = S.AppAudioOnly,
        Microphone = S.Microphone,
        MicDevice = S.MicDevice,
        MicVolume = (float)S.MicVolume,
        MicMuted = MicMuted,
        SeparateTracks = S.SeparateTracks,
        AudioBitrate = S.AudioBitrate * 1000,
        SystemOffset = S.SystemOffsetMs * 10_000L,
        MicOffset = S.MicOffsetMs * 10_000L,
    };

    // ── recording ───────────────────────────────────────────────────────────

    void SetState(StudioState s)
    {
        State = s;
        StateChanged?.Invoke();
    }

    /// <summary>Starts recording <paramref name="target"/> (or the current source) after the countdown.</summary>
    public async Task StartAsync(CaptureTarget? target = null)
    {
        if (State != StudioState.Idle) return;
        target ??= CurrentTarget();
        if (target == null) { Error?.Invoke(S.Source == "window" ? Loc.T("src.nowindow") : Loc.T("src.noregion")); return; }
        Target = target;
        SetState(StudioState.Arming);
        var cts = _arming = new CancellationTokenSource();
        var opts = Options(target);
        // the encoder and capture open while the countdown runs
        var prepare = Task.Run(() => Recorder.Prepare(opts));
        try
        {
            for (int n = Math.Max(0, S.Countdown); n > 0; n--)
            {
                CountdownTick?.Invoke(n);
                await Task.Delay(1000, cts.Token);
            }
            CountdownTick?.Invoke(0);
            var rec = await prepare;
            if (cts.IsCancellationRequested) { rec.Dispose(); SetState(StudioState.Idle); return; }
            Recorder = rec;
            rec.Failed += msg => _ui.BeginInvoke(() => Error?.Invoke(msg));
            rec.SourceLost += () => _ui.BeginInvoke(() => { if (State is StudioState.Recording or StudioState.Paused) _ = StopAsync(); });
            HookPreview(rec);
            rec.Begin();
            SetState(StudioState.Recording);
        }
        catch (OperationCanceledException)
        {
            _ = prepare.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); });
            SetState(StudioState.Idle);
        }
        catch (Exception ex)
        {
            Paths.Log("start: " + ex);
            SetState(StudioState.Idle);
            Error?.Invoke(ex.Message);
        }
    }

    public void CancelArming() => _arming?.Cancel();

    void HookPreview(Recorder rec)
    {
        _pump = new PreviewPump(rec.Gpu, 960, 540);
        _lastFrame = null;
        int skip = 0;
        _pump.Frame += (px, w, h) =>
        {
            _lastFrame = px; _lastW = w; _lastH = h;
            if (RecordingPreview == null || ++skip % 2 != 0) return;
            _ui.BeginInvoke(() =>
            {
                var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
                bmp.Freeze();
                RecordingPreview?.Invoke(bmp);
            });
        };
        rec.Preview = (gpu, tex, w, h) => _pump.Push(tex, w, h);
    }

    public void TogglePause()
    {
        if (Recorder == null) return;
        if (State == StudioState.Recording) { Recorder.Pause(); SetState(StudioState.Paused); }
        else if (State == StudioState.Paused) { Recorder.Resume(); SetState(StudioState.Recording); }
    }

    public void ToggleMic()
    {
        MicMuted = !MicMuted;
        Recorder?.SetMicMuted(MicMuted);
        StateChanged?.Invoke();
    }

    public void SetVolumes() => Recorder?.SetVolumes((float)S.SystemVolume, (float)S.MicVolume);

    public async Task StopAsync()
    {
        if (State == StudioState.Arming) { CancelArming(); return; }
        if (Recorder == null || State is not (StudioState.Recording or StudioState.Paused)) return;
        var rec = Recorder;
        SetState(StudioState.Finishing);
        var result = await Task.Run(() =>
        {
            var r = rec.Stop();
            rec.Preview = null;
            using (rec.Gpu.Enter()) _pump?.Dispose();
            rec.Dispose();
            return r;
        });
        _pump = null;
        Recorder = null;
        if (result.Error != null && result.Bytes < 1024)
        {
            try { File.Delete(result.Path); } catch { }
            SetState(StudioState.Idle);
            Error?.Invoke(result.Error);
            return;
        }
        string? thumb = SaveThumb(result.Path);
        var take = new Take(result.Path, DateTime.Now, result.Duration.TotalSeconds, result.Bytes, result.Width, result.Height, result.Fps, result.Encoder, thumb, result.Dropped);
        Takes.Insert(0, take);
        SaveTakes();
        SetState(StudioState.Idle);
        Saved?.Invoke(take);
        if (S.OpenFolderAfter) Core.Shell.Reveal(result.Path);
    }

    string? SaveThumb(string video)
    {
        if (_lastFrame == null) return null;
        try
        {
            string dir = Path.Combine(Paths.Data, "thumbs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(video.ToLowerInvariant())))[..16] + ".jpg");
            var bmp = BitmapSource.Create(_lastW, _lastH, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, _lastFrame, _lastW * 4);
            var enc = new JpegBitmapEncoder { QualityLevel = 88 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
            return path;
        }
        catch (Exception ex) { Paths.Log("thumb: " + ex.Message); return null; }
    }

    // ── takes ───────────────────────────────────────────────────────────────

    static string TakesFile => Path.Combine(Paths.Data, "takes.json");

    void LoadTakes()
    {
        if (Demo) return;
        try
        {
            if (File.Exists(TakesFile))
                Takes.AddRange((JsonSerializer.Deserialize<List<Take>>(File.ReadAllText(TakesFile)) ?? []).Where(t => File.Exists(t.Path)));
        }
        catch (Exception ex) { Paths.Log("takes: " + ex.Message); }
    }

    public void SaveTakes()
    {
        if (Demo) return;
        try { File.WriteAllText(TakesFile, JsonSerializer.Serialize(Takes.Take(60).ToList())); }
        catch (Exception ex) { Paths.Log("save takes: " + ex.Message); }
    }

    public void Forget(Take t)
    {
        Takes.Remove(t);
        SaveTakes();
    }
}
