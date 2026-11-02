using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using Path = System.IO.Path;
using SweaxIris.Capture;
using SweaxIris.Core;
using SweaxIris.Encode;
using SweaxIris.Recording;

namespace SweaxIris.Dev;

/// <summary>
/// <c>SweaxIris.exe --selftest DIR [--only a,b]</c>. Records only what this process draws itself (a GPU test
/// pattern and its own test window), never the desktop. Writes the files plus report.json for tools/analyze.py.
/// </summary>
public static class SelfTest
{
    static readonly List<object> Report = [];

    public static void Run(App app, string[] args)
    {
        string dir = Arg(args, "--selftest") ?? Path.Combine(Path.GetTempPath(), "sweaxiris-selftest");
        var only = Arg(args, "--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        Directory.CreateDirectory(dir);
        Paths.Init(Path.Combine(dir, "data"));
        bool Want(string n) => only == null || only.Contains(n);

        var thread = new Thread(() =>
        {
            try
            {
                if (Want("monitors")) foreach (var m in Studio.Monitors()) Log($"monitor {m.Number}: {m.Title} {m.Device} at {m.Bounds.Left},{m.Bounds.Top} {m.Bounds.Width}x{m.Bounds.Height}{(m.Primary ? " primary" : "")}");
                if (Want("encoders")) Encoders();
                var best = BestEncoder();
                Log($"best encoder: {best}");
                if (Want("sync")) Synthetic(dir, "sync", best, 1280, 720, 60, false, pauses: [(2.4, 3.65)], stopAt: 7.3, clockError: 0.0003);
                if (Want("drift")) Synthetic(dir, "drift", best, 640, 360, 30, false, pauses: [(5.2, 6.0), (11.4, 12.1)], stopAt: 20.5, clockError: -0.0008);
                if (Want("mkv")) Synthetic(dir, "mkv", best, 960, 540, 60, true, pauses: [(1.5, 2.2)], stopAt: 4.5, clockError: 0, separate: true);
                if (Want("perf"))
                {
                    foreach (var (enc, w, h, fps) in PerfMatrix())
                        Synthetic(dir, $"perf-{enc.Key}-{h}p{fps}", enc, w, h, fps, false, pauses: [], stopAt: 8, clockError: 0);
                }
                if (Want("window")) app.Dispatcher.Invoke(() => { }); // make sure the dispatcher is alive
                if (Want("window")) WindowTest(app, dir, best);
                if (Want("thumbs")) ThumbsTest(app, dir);
            }
            catch (Exception ex)
            {
                Log("FAILED: " + ex);
            }
            File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(Report, new JsonSerializerOptions { WriteIndented = true }));
            app.Dispatcher.Invoke(() => app.Shutdown());
        }) { IsBackground = true, Name = "SelfTest" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
    }

    static void Log(string s)
    {
        Console.WriteLine(s);
        Paths.Log("selftest: " + s);
    }

    static readonly Dictionary<uint, Gpu?> _probeGpus = [];

    static Gpu? GpuFor(uint vendor)
    {
        if (_probeGpus.TryGetValue(vendor, out var g)) return g;
        try { g = vendor == 0 ? Gpu.Create() : Gpu.Adapters().Any(a => a.VendorId == vendor) ? Gpu.Create(a => a.VendorId == vendor) : null; }
        catch { g = null; }
        return _probeGpus[vendor] = g;
    }

    static readonly Dictionary<string, string?> _probe = [];

    static void Encoders()
    {
        foreach (var a in Gpu.Adapters()) Log($"adapter {a.Index}: {a.Name} ({a.Vendor}) monitors={a.Monitors.Count} vram={a.VideoMemory / 1048576} MB");
        foreach (var e in EncoderSpec.All)
        {
            var why = VideoEncoder.Probe(e, GpuFor);
            _probe[e.Key] = why;
            Log($"{e,-28} {(why == null ? "OK" : "no: " + why)}");
            Report.Add(new { test = "probe", encoder = e.Key, ok = why == null, why });
        }
    }

    static EncoderSpec BestEncoder()
    {
        string[] order = ["nvenc-h264", "amf-h264", "qsv-h264", "x264-h264"];
        foreach (var k in order)
        {
            var e = EncoderSpec.FromKey(k)!;
            if (!_probe.TryGetValue(k, out var why)) _probe[k] = why = VideoEncoder.Probe(e, GpuFor);
            if (why == null) return e;
        }
        return EncoderSpec.FromKey("x264-h264")!;
    }

    static IEnumerable<(EncoderSpec, int, int, int)> PerfMatrix()
    {
        (string key, int w, int h, int fps)[] all =
        [
            ("nvenc-h264", 1920, 1080, 60), ("nvenc-h264", 2560, 1440, 144), ("nvenc-hevc", 3840, 2160, 60), ("nvenc-av1", 2560, 1440, 120),
            ("amf-h264", 1920, 1080, 60), ("amf-hevc", 2560, 1440, 60),
            ("qsv-h264", 1920, 1080, 60),
            ("x264-h264", 1920, 1080, 60), ("x264-h264", 1280, 720, 30),
        ];
        foreach (var (key, w, h, fps) in all)
        {
            var e = EncoderSpec.FromKey(key)!;
            if (!_probe.TryGetValue(key, out var why)) _probe[key] = why = VideoEncoder.Probe(e, GpuFor);
            if (why == null) yield return (e, w, h, fps);
        }
    }

    static void Synthetic(string dir, string name, EncoderSpec enc, int w, int h, int fps, bool mkv, (double From, double To)[] pauses, double stopAt, double clockError, bool separate = false)
    {
        string path = Path.Combine(dir, name + (mkv ? ".mkv" : ".mp4"));
        if (File.Exists(path)) File.Delete(path);
        // the pattern and the tone count from here; the recording starts exactly then, so the first flash is at 0
        long epoch = Clock.Now + 8_000_000;
        var o = new RecordingOptions
        {
            Target = CaptureTarget.Screen(IntPtr.Zero),
            OutputPath = path,
            Mkv = mkv,
            Encoder = enc,
            Fps = fps,
            SystemAudio = false,
            SeparateTracks = separate,
            TestVideo = g => new TestPattern(g, w, h, epoch),
            TestAudio = separate
                ? [("Tone", (i, t) => new TestTone(i, t, epoch, clockError)), ("Tone 2", (i, t) => new TestTone(i, t, epoch + 2_500_000, -clockError))]
                : [("Tone", (i, t) => new TestTone(i, t, epoch, clockError))],
        };
        Log($"── {name}: {enc} {w}x{h}@{fps}");
        using var rec = Recorder.Prepare(o);
        SleepUntil(epoch, 0);
        rec.Begin();
        var events = new List<object>();
        foreach (var (from, to) in pauses)
        {
            SleepUntil(epoch, from);
            rec.Pause();
            events.Add(new { pause = (Clock.Now - epoch) / 1e7 });
            SleepUntil(epoch, to);
            rec.Resume();
            events.Add(new { resume = (Clock.Now - epoch) / 1e7 });
        }
        SleepUntil(epoch, stopAt);
        var r = rec.Stop();
        Log($"   {r.Frames} frames, dropped {r.Dropped}, late {r.Late}, {r.Bytes / 1024} KB, {r.Duration.TotalSeconds:0.000}s {r.Error}");
        Report.Add(new { test = name, path, encoder = enc.Key, w, h, fps, mkv, separate, frames = r.Frames, dropped = r.Dropped, late = r.Late, bytes = r.Bytes, duration = r.Duration.TotalSeconds, error = r.Error, events, clockError, pauses = pauses.Select(p => new[] { p.From, p.To }).ToArray(), stopAt });
    }

    static void SleepUntil(long epoch, double seconds)
    {
        while (true)
        {
            double left = seconds - (Clock.Now - epoch) / 1e7;
            if (left <= 0) return;
            if (left > 0.03) Thread.Sleep(10);
            else Thread.SpinWait(200);
        }
    }

    /// <summary>
    /// Records this process's own test window through Windows.Graphics.Capture, with this process's own
    /// sound through app-only loopback: the real capture path, touching nothing else on the computer.
    /// </summary>
    static void WindowTest(App app, string dir, EncoderSpec enc)
    {
        string path = Path.Combine(dir, "window.mp4");
        if (File.Exists(path)) File.Delete(path);
        long epoch = Clock.Now + 30_000_000;
        var area = SystemParameters.WorkArea;
        using var card = new TestCardWindow(epoch, 40, 40, 960, 540);
        IntPtr hwnd = card.Handle;
        Thread.Sleep(600);
        using var tone = new TonePlayer(epoch, 0.01f); // -40 dBFS: barely audible, but the loopback hears it exactly
        Thread.Sleep(300);
        var o = new RecordingOptions
        {
            Target = CaptureTarget.OfWindow(hwnd),
            OutputPath = path,
            Encoder = enc,
            Fps = 60,
            Cursor = false,
            SystemAudio = true,
            AppAudioOnly = true,
        };
        Log($"── window: {enc}");
        using var rec = Recorder.Prepare(o);
        SleepUntil(epoch, 0);
        rec.Begin();
        SleepUntil(epoch, 3.4);
        rec.Pause();
        SleepUntil(epoch, 4.6);
        rec.Resume();
        SleepUntil(epoch, 8.3);
        var r = rec.Stop();
        Log($"   {r.Width}x{r.Height} {r.Frames} frames, dropped {r.Dropped}, late {r.Late}, {r.Bytes / 1024} KB, {r.Duration.TotalSeconds:0.000}s {r.Error} tone={tone.Error}");
        Report.Add(new { test = "window", path, encoder = enc.Key, w = r.Width, h = r.Height, fps = 60, frames = r.Frames, dropped = r.Dropped, late = r.Late, bytes = r.Bytes, duration = r.Duration.TotalSeconds, error = r.Error, pauses = new[] { new[] { 3.4, 4.6 } }, stopAt = 8.3, real = false, quiet = true, presents = card.Presents });
    }

    /// <summary>
    /// The source picker's live pictures, on this process's own topmost test card only: a window tile of the card
    /// and a region tile cropped strictly inside it. Saves the last picture of each and counts the refreshes.
    /// </summary>
    static void ThumbsTest(App app, string dir)
    {
        long epoch = Clock.Now;
        using var card = new TestCardWindow(epoch, 60, 60, 960, 540, topmost: true);
        Thread.Sleep(800);
        var frames = new Dictionary<string, int> { ["window"] = 0, ["region"] = 0 };
        var last = new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>();
        Thumbnails? thumbs = null;
        app.Dispatcher.Invoke(() =>
        {
            thumbs = new Thumbnails(app.Dispatcher);
            thumbs.Watch("window", Thumbnails.Window(card.Handle, 6), 6, 480, b => { frames["window"]++; last["window"] = b; });
            var r = new Native.Win32.RECT { Left = 160, Top = 140, Right = 160 + 640, Bottom = 140 + 360 };
            thumbs.Watch("region", Thumbnails.Region(r, 6), 6, 480, b => { frames["region"]++; last["region"] = b; });
        });
        Thread.Sleep(3000);
        app.Dispatcher.Invoke(() =>
        {
            foreach (var (k, b) in last) Snap.Save(b, Path.Combine(dir, $"thumb-{k}.png"));
            thumbs!.Dispose();
        });
        Log($"── thumbs: window {frames["window"]} pictures {(last.TryGetValue("window", out var w) ? $"{w.PixelWidth}x{w.PixelHeight}" : "none")}, region {frames["region"]} pictures {(last.TryGetValue("region", out var g) ? $"{g.PixelWidth}x{g.PixelHeight}" : "none")}");
        Report.Add(new { test = "thumbs", window = frames["window"], region = frames["region"] });
    }

    /// <summary>The window the real-capture test records: same stamp, flash and bars as the GPU pattern.</summary>
    sealed class TestWindow : Window
    {
        readonly long _epoch;
        readonly Rectangle[] _bits = new Rectangle[24];
        readonly Rectangle _flash, _bar;
        readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromRgb(24, 30, 40)) };

        public TestWindow(long epoch)
        {
            _epoch = epoch;
            Title = "SweaxIris test card";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            ShowInTaskbar = false;
            Width = 960;
            Height = 540;
            Left = SystemParameters.WorkArea.Right - 980;
            Top = SystemParameters.WorkArea.Bottom - 560;
            Content = _canvas;
            const int c = 22;
            for (int i = 0; i < 24; i++)
            {
                _bits[i] = new Rectangle { Width = c, Height = c };
                Canvas.SetLeft(_bits[i], c + i * c);
                Canvas.SetTop(_bits[i], c);
                _canvas.Children.Add(_bits[i]);
            }
            _flash = new Rectangle { Width = 135, Height = 135, Fill = Brushes.White };
            Canvas.SetLeft(_flash, 960 - 135 - c);
            Canvas.SetTop(_flash, 540 - 135 - c);
            _canvas.Children.Add(_flash);
            _bar = new Rectangle { Width = 110, Height = 54, Fill = new SolidColorBrush(Color.FromRgb(90, 160, 220)) };
            Canvas.SetTop(_bar, 200);
            _canvas.Children.Add(_bar);
            _canvas.Children.Add(new TextBlock { Text = "SweaxIris self-test", Foreground = Brushes.White, FontSize = 28, Margin = new Thickness(22, 470, 0, 0) });
            CompositionTarget.Rendering += OnRender;
            Closed += (_, _) => CompositionTarget.Rendering -= OnRender;
        }

        void OnRender(object? sender, EventArgs e)
        {
            long ms = (Clock.Now - _epoch) / 10_000;
            if (ms < 0) ms = 0;
            for (int i = 0; i < 24; i++) _bits[i].Fill = ((ms >> (23 - i)) & 1) == 1 ? Brushes.White : Brushes.Black;
            _flash.Visibility = ms % 1000 < 100 ? Visibility.Visible : Visibility.Hidden;
            Canvas.SetLeft(_bar, ms * 0.32 % 1070 - 110);
        }
    }
}
