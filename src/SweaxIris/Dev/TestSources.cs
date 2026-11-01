using System.Runtime.InteropServices;
using SweaxIris.Audio;
using SweaxIris.Capture;
using SweaxIris.Core;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SweaxIris.Dev;

/// <summary>
/// A synthetic picture drawn on the GPU on the recorder's own clock: a 24-bit stamp of the wall time in
/// milliseconds (so drops and repeats can be found), a white square for the first 100 ms of every second
/// (to check sync against <see cref="TestTone"/>), and moving bars so the encoder has real work.
/// </summary>
public sealed class TestPattern : IVideoSource
{
    readonly Gpu _gpu;
    readonly ID3D11Texture2D _tex;
    readonly ID3D11RenderTargetView _rtv;
    readonly ID3D11DeviceContext1 _ctx1;
    readonly long _epoch;

    public ID3D11Texture2D? Latest => _tex;
    public int Width { get; }
    public int Height { get; }
    public long Frames { get; private set; }
    public event Action? Lost { add { } remove { } }

    public TestPattern(Gpu gpu, int width, int height, long epoch)
    {
        _gpu = gpu;
        Width = width;
        Height = height;
        _epoch = epoch;
        _tex = gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _rtv = gpu.Device.CreateRenderTargetView(_tex);
        _ctx1 = gpu.Context.QueryInterface<ID3D11DeviceContext1>();
    }

    /// <summary>Block size of the time stamp, in pixels.</summary>
    public int Cell => Math.Max(8, Height / 24);

    public void Prepare(long now)
    {
        Draw(_ctx1, _rtv, Width, Height, (now - _epoch) / 10_000, Cell);
        Frames++;
    }

    /// <summary>Draws the pattern for wall time <paramref name="ms"/> into a render target.</summary>
    public static void Draw(ID3D11DeviceContext1 ctx, ID3D11RenderTargetView rtv, int width, int height, long ms, int cell)
    {
        double t = ms / 1000.0;
        // background: a slow colour drift and three bars moving at different speeds
        float hue = (float)(Math.Abs(t) * 0.11 % 1);
        ctx.ClearView(rtv, new Color4(0.10f + 0.10f * hue, 0.12f, 0.16f + 0.08f * (1 - hue), 1), Array.Empty<RawRect>());
        for (int b = 0; b < 3; b++)
        {
            int bw = width / 9;
            int x = (int)((Math.Abs(t) * (180 + b * 140)) % (width + bw)) - bw;
            int y0 = height / 4 + b * height / 6, y1 = y0 + height / 10;
            ctx.ClearView(rtv, new Color4(0.25f + 0.25f * b, 0.55f, 0.85f - 0.2f * b, 1), [new RawRect(Math.Max(0, x), y0, Math.Min(width, x + bw), y1)]);
        }
        // flash for the first 100 ms of every second
        if (ms >= 0 && ms % 1000 < 100)
        {
            int s = height / 4;
            ctx.ClearView(rtv, new Color4(1, 1, 1, 1), [new RawRect(width - s - cell, height - s - cell, width - cell, height - cell)]);
        }
        // the wall time in milliseconds, 24 bits, most significant first
        var ones = new List<RawRect>();
        var zeros = new List<RawRect>();
        long v = Math.Max(0, ms);
        for (int i = 0; i < 24; i++)
        {
            var r = new RawRect(cell + i * cell, cell, cell + i * cell + cell, 2 * cell);
            (((v >> (23 - i)) & 1) == 1 ? ones : zeros).Add(r);
        }
        if (ones.Count > 0) ctx.ClearView(rtv, new Color4(1, 1, 1, 1), ones.ToArray());
        if (zeros.Count > 0) ctx.ClearView(rtv, new Color4(0, 0, 0, 1), zeros.ToArray());
    }

    public void Dispose()
    {
        _ctx1.Dispose();
        _rtv.Dispose();
        _tex.Dispose();
    }
}

/// <summary>
/// A synthetic sound source on the system clock: 10 ms packets, a 1 kHz tone for the first 100 ms of every
/// second (matching <see cref="TestPattern"/>'s flash) and quiet noise otherwise. Optionally its sample clock
/// runs slightly fast or slow, like a real sound card, to exercise drift correction.
/// </summary>
public sealed unsafe class TestTone : IDisposable
{
    readonly Thread _thread;
    volatile bool _stop;

    public TestTone(MixerInput input, Timeline timeline, long epoch, double clockError = 0)
    {
        _thread = new Thread(() =>
        {
            const int n = 480;
            var buf = new float[n * 2];
            var rng = new Random(7);
            long sample = 0;
            long start = Clock.Now;
            double rate = 48000 * (1 + clockError);
            while (!_stop)
            {
                // the time this packet "was heard", from the card's (possibly drifting) sample clock
                long time = start + (long)(sample / rate * 10_000_000);
                long now = Clock.Now;
                if (time + 100_000 > now) { Thread.Sleep(1); continue; }
                for (int i = 0; i < n; i++)
                {
                    long ms = (time + (long)(i / rate * 10_000_000) - epoch) / 10_000;
                    double t = (sample + i) / rate;
                    float v = ms % 1000 < 100 ? (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * t)) : (float)((rng.NextDouble() - 0.5) * 0.002);
                    buf[2 * i] = v;
                    buf[2 * i + 1] = v;
                }
                long act = timeline.Active(time);
                if (act >= 0) fixed (float* p = buf) input.Push(p, n, Mixer.ToSamples(act));
                sample += n;
            }
        }) { IsBackground = true, Name = "SweaxIris.TestTone", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
    }
}

/// <summary>Plays a quiet tone through the default speakers for the first 100 ms of every second (for the real-capture test).</summary>
public sealed unsafe class TonePlayer : IDisposable
{
    readonly Thread _thread;
    volatile bool _stop;
    public string? Error { get; private set; }

    public TonePlayer(long epoch, float amplitude)
    {
        _thread = new Thread(() =>
        {
            try { Play(epoch, amplitude); }
            catch (Exception ex) { Error = ex.Message; Paths.Log("tone: " + ex); }
        }) { IsBackground = true, Name = "SweaxIris.TonePlayer", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    void Play(long epoch, float amplitude)
    {
        var en = Wasapi.Enumerator();
        Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var dev));
        Guid iid = Wasapi.IID_IAudioClient;
        Marshal.ThrowExceptionForHR(dev.Activate(ref iid, Wasapi.CLSCTX_ALL, IntPtr.Zero, out object o));
        var client = (IAudioClient)o;
        IntPtr fmt = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());
        Marshal.StructureToPtr(WAVEFORMATEXTENSIBLE.StereoFloat(), fmt, false);
        Marshal.ThrowExceptionForHR(client.Initialize(Wasapi.SHAREMODE_SHARED, Wasapi.STREAMFLAGS_AUTOCONVERTPCM | Wasapi.STREAMFLAGS_SRC_DEFAULT_QUALITY, 400_000, 0, fmt, IntPtr.Zero));
        client.GetBufferSize(out uint size);
        Guid riid = Wasapi.IID_IAudioRenderClient;
        Marshal.ThrowExceptionForHR(client.GetService(ref riid, out object ro));
        var render = (IAudioRenderClient)ro;
        Guid ckiid = Wasapi.IID_IAudioClock;
        Marshal.ThrowExceptionForHR(client.GetService(ref ckiid, out object co));
        var clock = (IAudioClock)co;
        clock.GetFrequency(out ulong freq);
        long written = 0;
        long start = 0;
        Marshal.ThrowExceptionForHR(client.Start());
        start = Clock.Now;
        while (!_stop)
        {
            client.GetCurrentPadding(out uint pad);
            uint free = size - pad;
            if (free >= 240)
            {
                Marshal.ThrowExceptionForHR(render.GetBuffer(free, out IntPtr data));
                var p = (float*)data;
                // the device clock says which sample is playing right now; later samples play after it
                clock.GetPosition(out ulong pos, out ulong qpc);
                double playing = pos / (double)freq * 48000;
                for (int i = 0; i < free; i++)
                {
                    long when = (long)qpc + (long)((written + i - playing) * 10_000_000 / 48000);
                    long ms = (when - epoch) / 10_000;
                    float v = ms >= 0 && ms % 1000 < 100 ? amplitude * MathF.Sin(2 * MathF.PI * 1000 * (written + i) / 48000f) : 0;
                    p[2 * i] = v;
                    p[2 * i + 1] = v;
                }
                render.ReleaseBuffer(free, 0);
                written += free;
            }
            Thread.Sleep(5);
        }
        client.Stop();
        Marshal.ReleaseComObject(clock);
        Marshal.ReleaseComObject(render);
        Marshal.ReleaseComObject(client);
        Marshal.ReleaseComObject(dev);
        Marshal.ReleaseComObject(en);
        Marshal.FreeHGlobal(fmt);
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
    }
}

/// <summary>
/// A plain Win32 window of this process that draws <see cref="TestPattern"/> with its own Direct3D swap chain
/// on every vertical blank. The real-capture test records it through Windows.Graphics.Capture.
/// </summary>
public sealed class TestCardWindow : IDisposable
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);

    static readonly WndProc Proc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
    readonly Thread _thread;
    volatile bool _stop;
    readonly ManualResetEventSlim _ready = new();
    public IntPtr Handle { get; private set; }
    public long Presents { get; private set; }

    public TestCardWindow(long epoch, int x, int y, int width, int height)
    {
        _thread = new Thread(() => Run(epoch, x, y, width, height)) { IsBackground = true, Name = "SweaxIris.TestCard" };
        _thread.Start();
        _ready.Wait(5000);
    }

    void Run(long epoch, int x, int y, int width, int height)
    {
        var wc = new WNDCLASSEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = Native.Win32.GetModuleHandle(null), lpszClassName = "SweaxIrisTestCard",
        };
        RegisterClassExW(ref wc);
        const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
        Handle = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, "SweaxIrisTestCard", "SweaxIris test card", WS_POPUP | WS_VISIBLE, x, y, width, height, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        using var gpu = Gpu.Create();
        using var factory = gpu.Device.QueryInterface<IDXGIDevice>().GetParent<IDXGIAdapter>().GetParent<IDXGIFactory2>();
        using var swap = factory.CreateSwapChainForHwnd(gpu.Device, Handle, new SwapChainDescription1
        {
            Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
        });
        using var ctx1 = gpu.Context.QueryInterface<ID3D11DeviceContext1>();
        _ready.Set();
        int cell = Math.Max(8, height / 24);
        while (!_stop)
        {
            while (Native.Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
            {
                Native.Win32.TranslateMessage(ref msg);
                Native.Win32.DispatchMessage(ref msg);
            }
            using (var back = swap.GetBuffer<ID3D11Texture2D>(0))
            using (var rtv = gpu.Device.CreateRenderTargetView(back))
                TestPattern.Draw(ctx1, rtv, width, height, (Clock.Now - epoch) / 10_000, cell);
            swap.Present(1, PresentFlags.None);
            Presents++;
        }
        DestroyWindow(Handle);
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
    }
}
