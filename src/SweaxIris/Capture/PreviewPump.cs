using SweaxIris.Core;
using SweaxIris.Native;
using SweaxIris.Recording;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SweaxIris.Capture;

/// <summary>
/// Shrinks frames to a small BGRA picture on the GPU and reads it back one frame later (so it never waits
/// on the GPU). Used for the viewfinder, the take thumbnails and the saved-toast.
/// </summary>
public sealed unsafe class PreviewPump : IDisposable
{
    readonly Gpu _gpu;
    readonly int _maxW, _maxH;
    Converter? _conv;
    readonly ID3D11Texture2D?[] _staging = new ID3D11Texture2D?[2];
    readonly bool[] _pending = new bool[2];
    int _i;

    /// <summary>Raised on the capturing thread with a fresh copy: pixels (BGRA), width, height.</summary>
    public event Action<byte[], int, int>? Frame;

    public PreviewPump(Gpu gpu, int maxW = 640, int maxH = 360)
    {
        _gpu = gpu;
        _maxW = maxW;
        _maxH = maxH;
    }

    /// <summary>Call with the GPU held.</summary>
    public void Push(ID3D11Texture2D src, int sw, int sh)
    {
        double s = Math.Min((double)_maxW / sw, (double)_maxH / sh);
        int w = Math.Max(2, (int)(sw * Math.Min(1, s)) & ~1), h = Math.Max(2, (int)(sh * Math.Min(1, s)) & ~1);
        if (_conv == null || _conv.Width != w || _conv.Height != h)
        {
            _conv?.Dispose();
            for (int k = 0; k < 2; k++) { _staging[k]?.Dispose(); _staging[k] = null; _pending[k] = false; }
            _conv = new Converter(_gpu, w, h, Format.B8G8R8A8_UNorm);
            for (int k = 0; k < 2; k++)
                _staging[k] = _gpu.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
                });
        }
        _conv.Run(src, sw, sh);
        _gpu.Context.CopyResource(_staging[_i]!, _conv.Output);
        _pending[_i] = true;
        int j = 1 - _i;
        if (_pending[j] && _gpu.Context.Map(_staging[j]!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var map).Success)
        {
            var bytes = new byte[w * h * 4];
            fixed (byte* dst = bytes)
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst + y * w * 4, w * 4, w * 4);
            _gpu.Context.Unmap(_staging[j]!, 0);
            _pending[j] = false;
            Frame?.Invoke(bytes, w, h);
        }
        _i = j;
    }

    public void Dispose()
    {
        _conv?.Dispose();
        foreach (var s in _staging) s?.Dispose();
    }
}

/// <summary>Captures the chosen source at a gentle rate just for the viewfinder, while nothing is recording.</summary>
public sealed class IdlePreview : IDisposable
{
    readonly Gpu _gpu;
    readonly IVideoSource _source;
    readonly PreviewPump _pump;
    readonly Thread _thread;
    volatile bool _stop;

    public event Action<byte[], int, int>? Frame { add => _pump.Frame += value; remove => _pump.Frame -= value; }
    public int SourceWidth => _source.Width;
    public int SourceHeight => _source.Height;

    public IdlePreview(CaptureTarget t, bool cursor, Func<Gpu, IVideoSource>? test = null)
    {
        _gpu = Gpu.Create(a => t.Monitor == IntPtr.Zero || a.Monitors.Contains(t.Monitor));
        try
        {
            var every = TimeSpan.FromMilliseconds(40);
            if (test != null) _source = test(_gpu);
            else if (t.Kind == SourceKind.Window) _source = WgcSource.ForWindow(_gpu, t.Window, cursor, every);
            else
            {
                Win32.RECT? crop = null;
                if (t.Kind == SourceKind.Region)
                {
                    var mon = Win32.MonitorInfo(t.Monitor).rcMonitor;
                    crop = new Win32.RECT { Left = t.Region.Left - mon.Left, Top = t.Region.Top - mon.Top, Right = t.Region.Right - mon.Left, Bottom = t.Region.Bottom - mon.Top };
                }
                _source = WgcSource.ForMonitor(_gpu, t.Monitor, crop, cursor, every);
            }
        }
        catch
        {
            _gpu.Dispose();
            throw;
        }
        _pump = new PreviewPump(_gpu);
        _thread = new Thread(Run) { IsBackground = true, Name = "SweaxIris.Preview", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    void Run()
    {
        while (!_stop)
        {
            try
            {
                using (_gpu.Enter())
                {
                    _source.Prepare(Clock.Now);
                    if (_source.Latest is { } tex) _pump.Push(tex, _source.Width, _source.Height);
                }
            }
            catch (Exception ex) { Paths.Log("preview: " + ex.Message); Thread.Sleep(500); }
            Thread.Sleep(40);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
        _source.Dispose();
        using (_gpu.Enter()) _pump.Dispose();
        _gpu.Dispose();
    }
}
