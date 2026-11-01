using SweaxIris.Core;
using SweaxIris.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace SweaxIris.Capture;

/// <summary>Anything the recorder can take pictures from.</summary>
public interface IVideoSource : IDisposable
{
    /// <summary>The newest picture. Read it only while holding <see cref="Gpu.Enter"/>.</summary>
    ID3D11Texture2D? Latest { get; }
    int Width { get; }
    int Height { get; }
    long Frames { get; }
    /// <summary>The window being recorded closed, or the monitor went away.</summary>
    event Action? Lost;
    /// <summary>
    /// Called on the recorder's clock just before a frame is taken, with the moment that frame stands for
    /// (sources keeping several frames pick the matching one, test sources draw it).
    /// </summary>
    void Prepare(long when100ns) { }

    /// <summary>How far behind real time the recorder should look so the right frame has arrived (100 ns).</summary>
    long DeliveryLatency => 0;
}

/// <summary>
/// Windows.Graphics.Capture on a monitor (optionally one rectangle of it) or a window. Every frame that
/// arrives is copied into <see cref="Latest"/>; the recorder's clock decides which ones end up in the file.
/// </summary>
public sealed class WgcSource : IVideoSource
{
    static bool? _borderless;
    static readonly bool HasMinInterval = ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval");
    static readonly bool HasBorder = ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");
    static readonly bool HasSecondary = ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IncludeSecondaryWindows");

    readonly Gpu _gpu;
    readonly GraphicsCaptureItem _item;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    readonly bool _window;
    readonly Win32.RECT? _crop;
    SizeInt32 _poolSize;
    // the last few frames with the moment Windows composed them, so the recorder can take the one that
    // belongs to each tick instead of whatever arrived last
    readonly int Ring;
    readonly ID3D11Texture2D?[] _ring;
    readonly long[] _times;
    int _write, _filled;
    ID3D11Texture2D? _latest;
    long _frames;
    bool _disposed;
    double _latency = 50_000;
    readonly int[] _ages = new int[12];
    long _lastPicked, _samePicks, _noneOlder, _gapMax, _lastArrival;

    public ID3D11Texture2D? Latest => _latest;
    /// <summary>How long frames take to reach us after Windows composed them (100 ns, a high percentile).</summary>
    public long DeliveryLatency => (long)_latency;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long Frames => Interlocked.Read(ref _frames);
    public event Action? Lost;

    /// <summary>Asks once for capture without the yellow frame Windows draws around a captured window.</summary>
    public static async Task AllowBorderlessAsync()
    {
        if (_borderless != null) return;
        try
        {
            if (ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
            {
                var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                _borderless = status == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
            }
            else _borderless = false;
        }
        catch (Exception ex) { _borderless = false; Paths.Log("borderless: " + ex.Message); }
    }

    /// <param name="every">Least time between frames (0 = every refresh, for recording).</param>
    /// <param name="ring">How many recent frames to keep (the recorder wants a few; a thumbnail needs one).</param>
    public static WgcSource ForMonitor(Gpu gpu, IntPtr hmon, Win32.RECT? crop, bool cursor, TimeSpan every = default, int ring = 6)
        => new(gpu, WinRtInterop.ForMonitor(hmon), window: false, crop, cursor, every, ring);

    public static WgcSource ForWindow(Gpu gpu, IntPtr hwnd, bool cursor, TimeSpan every = default, int ring = 6)
        => new(gpu, WinRtInterop.ForWindow(hwnd), window: true, null, cursor, every, ring);

    WgcSource(Gpu gpu, GraphicsCaptureItem item, bool window, Win32.RECT? crop, bool cursor, TimeSpan every, int ring)
    {
        Ring = Math.Max(1, ring);
        _ring = new ID3D11Texture2D?[Ring];
        _times = new long[Ring];
        _gpu = gpu;
        _item = item;
        _window = window;
        _poolSize = item.Size;
        if (crop is { } c)
        {
            // keep the rectangle inside the monitor
            int l = Math.Clamp(c.Left, 0, _poolSize.Width - 2), t = Math.Clamp(c.Top, 0, _poolSize.Height - 2);
            int r = Math.Clamp(c.Right, l + 2, _poolSize.Width), b = Math.Clamp(c.Bottom, t + 2, _poolSize.Height);
            _crop = new Win32.RECT { Left = l, Top = t, Right = r, Bottom = b };
        }
        Width = _crop?.Width ?? _poolSize.Width;
        Height = _crop?.Height ?? _poolSize.Height;

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRt, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = cursor;
        try { if (HasBorder && _borderless == true) _session.IsBorderRequired = false; } catch (Exception ex) { Paths.Log("border: " + ex.Message); }
        // without this Windows hands out at most ~60 frames a second; take every refresh so each tick finds the right one
        try { if (HasMinInterval) _session.MinUpdateInterval = every > TimeSpan.Zero ? every : TimeSpan.FromMilliseconds(1); } catch (Exception ex) { Paths.Log("interval: " + ex.Message); }
        try { if (window && HasSecondary) _session.IncludeSecondaryWindows = true; } catch { }
        item.Closed += (_, _) => Lost?.Invoke();
        _session.StartCapture();
    }

    void OnFrame(Direct3D11CaptureFramePool pool, object args)
    {
        if (_disposed) return;
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame == null) return;
            var content = frame.ContentSize;
            using var tex = WinRtInterop.TextureOf(frame.Surface);
            var desc = tex.Description;
            int w, h;
            Box box;
            if (_crop is { } c)
            {
                w = c.Width; h = c.Height;
                box = new Box(c.Left, c.Top, 0, c.Right, c.Bottom, 1);
            }
            else
            {
                w = Math.Min(content.Width, (int)desc.Width);
                h = Math.Min(content.Height, (int)desc.Height);
                if (w < 2 || h < 2) return;
                box = new Box(0, 0, 0, w, h, 1);
            }
            long composed = frame.SystemRelativeTime.Ticks;
            if (_lastArrival != 0) _gapMax = Math.Max(_gapMax, composed - _lastArrival);
            _lastArrival = composed;
            long lat = Clock.Now - composed;
            // a slowly falling peak follower: the recorder looks this far into the past
            if (lat is > 0 and < 2_000_000) _latency = lat > _latency ? lat : _latency * 0.995 + lat * 0.005;
            using (_gpu.Enter())
            {
                ref var slot = ref _ring[_write];
                if (slot == null || (int)slot.Description.Width != w || (int)slot.Description.Height != h)
                {
                    if (slot != null && ((int)slot.Description.Width != w || (int)slot.Description.Height != h))
                    {
                        // new size (a resized window): forget the old frames
                        for (int i = 0; i < Ring; i++) if (_ring[i] != null && !ReferenceEquals(_ring[i], _latest)) { _ring[i]!.Dispose(); _ring[i] = null; }
                        _filled = 0;
                    }
                    slot = _gpu.Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    });
                }
                _gpu.Context.CopySubresourceRegion(slot, 0, 0, 0, 0, tex, 0, box);
                _times[_write] = composed;
                _write = (_write + 1) % Ring;
                _filled = Math.Min(Ring, _filled + 1);
                if (_latest == null) { _latest = slot; Width = w; Height = h; }
            }
            Interlocked.Increment(ref _frames);
            // a window that changed size: let the next frames come at the new size
            if (_window && (content.Width != _poolSize.Width || content.Height != _poolSize.Height) && content.Width > 0 && content.Height > 0)
            {
                _poolSize = content;
                pool.Recreate(_gpu.WinRt, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
            }
        }
        catch (Exception ex)
        {
            Paths.Log("capture frame: " + ex.Message);
        }
    }

    /// <summary>Picks the newest frame composed at or before <paramref name="when"/> (call with the GPU held).</summary>
    public void Prepare(long when)
    {
        int best = -1, oldest = -1;
        for (int k = 0; k < _filled; k++)
        {
            int i = ((_write - 1 - k) % Ring + Ring) % Ring;
            if (_ring[i] == null) continue;
            if (oldest < 0 || _times[i] < _times[oldest]) oldest = i;
            if (_times[i] <= when && (best < 0 || _times[i] > _times[best])) best = i;
        }
        int pick = best >= 0 ? best : oldest;
        if (pick < 0) return;
        long age = when - _times[pick];
        _ages[Math.Clamp((int)(age / 20_000), 0, _ages.Length - 1)]++;
        if (_times[pick] == _lastPicked) _samePicks++;
        _lastPicked = _times[pick];
        if (best < 0) _noneOlder++;
        var tex = _ring[pick]!;
        if (!ReferenceEquals(tex, _latest))
        {
            // a texture that left the ring after a resize is only kept while it was the current one
            if (_latest != null && Array.IndexOf(_ring, _latest) < 0) _latest.Dispose();
            _latest = tex;
            Width = (int)tex.Description.Width;
            Height = (int)tex.Description.Height;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Frames > 0) Paths.Log($"capture: {Frames} frames, delivery latency ~{_latency / 10_000:0.0} ms, picked-age hist(2ms) [{string.Join(",", _ages)}], same {_samePicks}, none-older {_noneOlder}, longest gap between frames {_gapMax / 10_000.0:0.0} ms");
        try { _pool.FrameArrived -= OnFrame; } catch { }
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        using (_gpu.Enter())
        {
            if (_latest != null && Array.IndexOf(_ring, _latest) < 0) _latest.Dispose();
            _latest = null;
            for (int i = 0; i < Ring; i++) { _ring[i]?.Dispose(); _ring[i] = null; }
        }
    }
}
