using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SweaxIris.Core;
using SweaxIris.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SweaxIris.Capture;

/// <summary>
/// Live pictures for the source picker: every monitor and window tile gets a small capture that refreshes
/// a few times a second (the chosen one faster). One GPU device, one worker thread, small BGRA read-backs.
/// </summary>
public sealed unsafe class Thumbnails : IDisposable
{
    sealed class Entry
    {
        public required IVideoSource Source;
        public required Action<BitmapSource> OnFrame;
        public double Fps;
        public long Next;
        public int W, H;
        public ID3D11Texture2D? Staging;
        public Converter? Conv;
    }

    readonly Dispatcher _ui;
    readonly Gpu _gpu;
    readonly Dictionary<string, Entry> _entries = [];
    readonly object _lock = new();
    readonly Thread _thread;
    volatile bool _stop;

    public Thumbnails(Dispatcher ui)
    {
        _ui = ui;
        _gpu = Gpu.Create();
        _thread = new Thread(Run) { IsBackground = true, Name = "SweaxIris.Thumbs", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public bool Has(string key) { lock (_lock) return _entries.ContainsKey(key); }

    /// <summary>Starts (or retunes) a live tile. Returns false when the source cannot be captured.</summary>
    public bool Watch(string key, Func<Gpu, IVideoSource> create, double fps, int maxW, Action<BitmapSource> onFrame)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var e))
            {
                e.Fps = fps;
                e.OnFrame = onFrame;
                return true;
            }
        }
        IVideoSource src;
        try { src = create(_gpu); }
        catch (Exception ex) { Paths.Log($"thumb {key}: {ex.Message}"); return false; }
        lock (_lock) _entries[key] = new Entry { Source = src, OnFrame = onFrame, Fps = fps, W = maxW, H = maxW * 9 / 16 };
        return true;
    }

    public void Rate(string key, double fps)
    {
        lock (_lock) if (_entries.TryGetValue(key, out var e)) { e.Fps = fps; e.Next = 0; }
    }

    public void Unwatch(string key)
    {
        Entry? e;
        lock (_lock) { if (!_entries.Remove(key, out e)) return; }
        Release(e);
    }

    /// <summary>Stops every tile whose key is not in <paramref name="keep"/>.</summary>
    public void Keep(IEnumerable<string> keep)
    {
        var set = keep.ToHashSet();
        List<string> drop;
        lock (_lock) drop = _entries.Keys.Where(k => !set.Contains(k)).ToList();
        foreach (var k in drop) Unwatch(k);
    }

    void Release(Entry e)
    {
        try { e.Source.Dispose(); } catch { }
        using (_gpu.Enter())
        {
            e.Conv?.Dispose();
            e.Staging?.Dispose();
        }
    }

    void Run()
    {
        while (!_stop)
        {
            Thread.Sleep(50);
            List<(string, Entry)> due;
            long now = Clock.Now;
            lock (_lock) due = _entries.Where(kv => kv.Value.Next <= now).Select(kv => (kv.Key, kv.Value)).ToList();
            foreach (var (key, e) in due)
            {
                e.Next = now + (long)(10_000_000 / Math.Max(0.2, e.Fps));
                try { Grab(e); }
                catch (Exception ex) { Paths.Log($"thumb {key}: {ex.Message}"); e.Next = now + 30_000_000; }
            }
        }
    }

    void Grab(Entry e)
    {
        byte[] px;
        int w, h;
        using (_gpu.Enter())
        {
            e.Source.Prepare(Clock.Now);
            var tex = e.Source.Latest;
            if (tex == null) return;
            int sw = e.Source.Width, sh = e.Source.Height;
            w = Math.Max(2, (int)(sw * Math.Min(1, (double)e.W / sw)) & ~1);
            h = Math.Max(2, (int)(sh * Math.Min(1, (double)e.W / sw)) & ~1);
            if (e.Conv == null || e.Conv.Width != w || e.Conv.Height != h)
            {
                e.Conv?.Dispose();
                e.Staging?.Dispose();
                e.Conv = new Converter(_gpu, w, h, Format.B8G8R8A8_UNorm);
                e.Staging = _gpu.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
                });
            }
            e.Conv.Run(tex, sw, sh);
            _gpu.Context.CopyResource(e.Staging!, e.Conv.Output);
            var map = _gpu.Context.Map(e.Staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            px = new byte[w * h * 4];
            fixed (byte* dst = px)
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst + y * w * 4, w * 4, w * 4);
            _gpu.Context.Unmap(e.Staging!, 0);
        }
        var cb = e.OnFrame;
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            cb(bmp);
        });
    }

    public static Func<Gpu, IVideoSource> Monitor(IntPtr hmon, double fps) => g => WgcSource.ForMonitor(g, hmon, null, false, TimeSpan.FromSeconds(1 / fps), ring: 1);
    public static Func<Gpu, IVideoSource> Window(IntPtr hwnd, double fps) => g => WgcSource.ForWindow(g, hwnd, false, TimeSpan.FromSeconds(1 / fps), ring: 1);

    public static Func<Gpu, IVideoSource> Region(Win32.RECT r, double fps) => g =>
    {
        var mon = Win32.MonitorFromRect(ref r, Win32.MONITOR_DEFAULTTONEAREST);
        var b = Win32.MonitorInfo(mon).rcMonitor;
        return WgcSource.ForMonitor(g, mon, new Win32.RECT { Left = r.Left - b.Left, Top = r.Top - b.Top, Right = r.Right - b.Left, Bottom = r.Bottom - b.Top }, false, TimeSpan.FromSeconds(1 / fps), ring: 1);
    };

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
        List<Entry> all;
        lock (_lock) { all = _entries.Values.ToList(); _entries.Clear(); }
        foreach (var e in all) Release(e);
        _gpu.Dispose();
    }
}
