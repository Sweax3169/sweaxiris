using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SweaxIris.Core;
using SweaxIris.Native;
using SweaxIris.Recording;
using SweaxIris.UI;
using Path = System.IO.Path;

namespace SweaxIris.Dev;

/// <summary>
/// <c>SweaxIris.exe --shots DIR [--theme dark|light] [--lang en|tr] [--only a,b] [--palette x]</c>
/// Renders the README and site pictures from the real windows, driven frame by frame on the spring clock,
/// with made-up content (a drawn game, a drawn desktop, invented monitors and devices). Nothing real is captured.
/// Animated scenes are written as PNG frame folders; tools/encode-media.ps1 turns them into MP4 and GIF.
/// </summary>
public static class Shots
{
    const double Fps = 30;
    static string _dir = "";
    static string _suffix = "";

    public static void Run(App app, string[] args)
    {
        string? Arg(string a) { int i = Array.FindIndex(args, x => x.Equals(a, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        _dir = Arg("--shots") ?? Path.Combine(Path.GetTempPath(), "sweaxiris-shots");
        string theme = Arg("--theme") ?? "dark", lang = Arg("--lang") ?? "en", palette = Arg("--palette") ?? "coating";
        var only = Arg("--only")?.Split(',').ToHashSet();
        bool Want(string n) => only == null || only.Contains(n);
        _suffix = $"{lang}-{theme}";
        Directory.CreateDirectory(_dir);
        Paths.Init(Path.Combine(Path.GetTempPath(), "sweaxiris-shots-data"));
        Loc.Set(lang);
        Theme.Apply(theme, palette);
        Springs.ManualClock = true;
        SetUpDemo();

        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (Want("main")) MainStill();
                if (Want("settings")) SettingsStills();
                if (Want("palettes")) Palettes(theme);
                if (Want("bar")) BarStill();
                if (Want("select")) SelectStill();
                if (Want("film-record")) await FilmRecord();
                if (Want("film-region")) await FilmRegion();
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(_dir, "error.txt"), ex.ToString()); }
            app.Shutdown();
        });
    }

    // ── made-up world ───────────────────────────────────────────────────────

    static void SetUpDemo()
    {
        Studio.Demo = true;
        Studio.DemoMonitors =
        [
            new(IntPtr.Zero, @"\\.\DISPLAY2", "Side 27", new Win32.RECT { Left = -2560, Top = 0, Right = 0, Bottom = 1440 }, false, 144, 1),
            new(IntPtr.Zero, @"\\.\DISPLAY1", "Main 27", new Win32.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1440 }, true, 240, 2),
            new(IntPtr.Zero, @"\\.\DISPLAY3", "Portrait 24", new Win32.RECT { Left = 2560, Top = -516, Right = 4000, Bottom = 2044 }, false, 60, 3),
        ];
        Studio.DemoWindows = [("Highway Dusk — racing", "dusk.exe"), ("Atlas — Live dashboard", "atlas.exe"), ("notes.md — Quill", "quill.exe"), ("Voice chat", "voice.exe")];
        MainWindow.DemoSystemDevice = Loc.Turkish ? "Hoparlörler" : "Speakers";
        MainWindow.DemoMicDevice = Loc.Turkish ? "Stüdyo mikrofonu" : "Studio microphone";
        MainWindow.DemoSource = ("Main 27", 2560, 1440);
    }

    static Studio NewStudio()
    {
        var s = new Settings { Monitor = @"\\.\DISPLAY1", Microphone = true, Fps = 60, Resolution = 1440, Quality = 0.72, KeyRecord = "Ctrl+Alt+R", KeyRegion = "Ctrl+Alt+A", KeyPause = "Pause", KeyMic = "Ctrl+Alt+M" };
        var studio = new Studio(s, Application.Current.Dispatcher);
        studio.DemoEncoders();
        return studio;
    }

    static BitmapSource Game(double t, int w = 960, int h = 540) => Snap.Render(new GameScene { Time = t }, w, h, 1);

    static MainWindow OpenMain(Studio studio, double width = 1060, double height = 760)
    {
        var main = new MainWindow(studio) { Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        main.Show();
        Settle();
        return main;
    }

    /// <summary>Lets layout, bindings and springs finish.</summary>
    static void Settle(int frames = 90)
    {
        for (int i = 0; i < frames; i++) { Springs.Step(1 / Fps); Pump(); }
    }

    static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>The window content on a soft backdrop, with rounded corners and a shadow.</summary>
    static BitmapSource Stage(FrameworkElement content, double w, double h, double scale = 2, double pad = 36)
    {
        var shot = Snap.Render(content, w, h, scale);
        var stage = new Grid { Width = w + pad * 2, Height = h + pad * 2, Background = StageBrush() };
        var frame = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Background = new ImageBrush(shot) { Stretch = Stretch.Fill },
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), BorderThickness = new Thickness(Theme.Dark ? 1 : 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Opacity = Theme.Dark ? 0.6 : 0.25, Direction = 270 },
        };
        stage.Children.Add(frame);
        return Snap.Render(stage, stage.Width, stage.Height, scale);
    }

    static Brush StageBrush() => Theme.Dark
        ? new LinearGradientBrush(Color.FromRgb(0x14, 0x18, 0x1F), Color.FromRgb(0x08, 0x0A, 0x0D), 90)
        : new LinearGradientBrush(Color.FromRgb(0xF3, 0xF5, 0xF8), Color.FromRgb(0xD9, 0xDE, 0xE5), 90);

    static string Out(string name) => Path.Combine(_dir, $"{name}-{_suffix}.png");

    // ── stills ──────────────────────────────────────────────────────────────

    static void MainStill()
    {
        var studio = NewStudio();
        studio.DemoSystemPeak = () => 0.3f;
        studio.DemoMicPeak = () => 0.12f;
        var main = OpenMain(studio);
        main.ShowFrame(Game(3.2));
        Settle(30);
        Snap.Save(Stage((FrameworkElement)main.Content, main.ActualWidth, main.ActualHeight), Out("main"));

        studio.DemoSet(CaptureTarget.Screen(IntPtr.Zero));
        studio.DemoElapsed = TimeSpan.FromSeconds(83);
        studio.DemoBytes = 214L << 20;
        studio.DemoSystemPeak = () => 0.42f;
        studio.DemoMicPeak = () => 0.18f;
        studio.DemoSet(StudioState.Recording);
        main.ShowFrame(Game(5.0));
        Settle(60);
        main.Tick();
        Snap.Save(Stage((FrameworkElement)main.Content, main.ActualWidth, main.ActualHeight), Out("main-recording"));
        main.Close();
    }

    static void SettingsStills()
    {
        var studio = NewStudio();
        var main = OpenMain(studio);
        main.ShowSettings(true);
        Settle(40);
        foreach (var page in new[] { "general", "keys", "video", "sound", "look" })
        {
            main.SettingsPage(page);
            Settle(40);
            Snap.Save(Stage((FrameworkElement)main.Content, main.ActualWidth, main.ActualHeight), Out("settings-" + page));
        }
        main.Close();
    }

    static void Palettes(string theme)
    {
        var cells = new UniformGrid { Columns = 3 };
        foreach (var p in Theme.Palettes)
        {
            Theme.Apply(theme, p.Key);
            var studio = NewStudio();
            var main = OpenMain(studio);
            main.ShowFrame(Game(2 + Array.IndexOf(Theme.Palettes, p)));
            Settle(30);
            var bmp = Snap.Render((FrameworkElement)main.Content, main.ActualWidth, main.ActualHeight, 1);
            cells.Children.Add(new Border { Margin = new Thickness(10), CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = new Image { Source = bmp, Width = 520, Height = 350, Stretch = Stretch.Uniform } });
            main.Close();
        }
        Theme.Apply(theme, "coating");
        var stage = new Border { Background = StageBrush(), Padding = new Thickness(20), Child = cells };
        Snap.Save(Snap.Render(stage, 3 * 540 + 40, 2 * 370 + 40, 1.5), Out("palettes"));
    }

    static void BarStill()
    {
        var studio = NewStudio();
        studio.DemoElapsed = TimeSpan.FromSeconds(754);
        studio.DemoSystemPeak = () => 0.5f;
        studio.DemoMicPeak = () => 0.25f;
        studio.DemoSet(CaptureTarget.Screen(IntPtr.Zero));
        studio.DemoSet(StudioState.Recording);
        var bar = new ControllerWindow(studio) { Left = -30000, Top = -30000 };
        bar.Show();
        Settle(40);
        bar.DemoTick();
        var content = (FrameworkElement)bar.Content;
        var stage = new Grid { Background = StageBrush() };
        stage.Children.Add(new Image { Source = Snap.Render(content, content.ActualWidth, content.ActualHeight, 2), Width = content.ActualWidth, Height = content.ActualHeight });
        Snap.Save(Snap.Render(stage, content.ActualWidth + 40, content.ActualHeight + 30, 2), Out("bar"));
        bar.Close();
    }

    static void SelectStill()
    {
        var (w, h) = (1600.0, 900.0);
        var desk = new DesktopScene { Time = 2 };
        var deskBmp = Snap.Render(desk, w, h, 1);
        var sel = new Rect(desk.ChartWindow.X + 12, desk.ChartWindow.Y + 40, desk.ChartWindow.Width - 24, desk.ChartWindow.Height - 52);
        var overlay = RegionSelector.DemoVisual(w, h, sel, "adjust", new Point(sel.Right - 40, sel.Bottom - 30), null);
        var g = new Grid { Width = w, Height = h };
        g.Children.Add(new Image { Source = deskBmp });
        g.Children.Add(overlay);
        Snap.Save(Stage(g, w, h, 1.25, 30), Out("select"));
    }

    // ── films (PNG frames) ──────────────────────────────────────────────────

    sealed class Film(string name)
    {
        readonly string _dir = Path.Combine(Shots._dir, "frames", $"{name}-{Shots._suffix}");
        int _n;
        public void Start() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); Directory.CreateDirectory(_dir); }
        public void Frame(BitmapSource b) => Snap.Save(b, Path.Combine(_dir, $"{_n++:00000}.png"));
    }

    /// <summary>Main window: hover, click, 3-2-1, recording with the take growing, pause, resume, stop, the take lands.</summary>
    static async Task FilmRecord()
    {
        var studio = NewStudio();
        var main = OpenMain(studio);
        var film = new Film("record");
        film.Start();
        double t = 0;
        var rng = new Random(3);
        float sys = 0, mic = 0;
        studio.DemoSystemPeak = () => sys;
        studio.DemoMicPeak = () => mic;
        void Step(int frames, Action<int>? each = null)
        {
            for (int i = 0; i < frames; i++)
            {
                t += 1 / Fps;
                sys = (float)(0.25 + 0.2 * Math.Abs(Math.Sin(t * 7)) + rng.NextDouble() * 0.15);
                mic = (float)(Math.Sin(t * 1.3) > 0.2 ? 0.1 + rng.NextDouble() * 0.25 : 0.01);
                each?.Invoke(i);
                main.ShowFrame(Game(t, 640, 360));
                Springs.Step(1 / Fps);
                Pump();
                main.Tick();
                film.Frame(Stage((FrameworkElement)main.Content, main.ActualWidth, main.ActualHeight, 1.25, 28));
            }
        }
        Step(20);
        main.DemoHover(true);
        Step(16);
        studio.DemoSet(CaptureTarget.Screen(IntPtr.Zero));
        studio.DemoSet(StudioState.Arming);
        for (int n = 3; n >= 1; n--) { main.DemoCount(n); Step(24); }
        main.DemoCount(0);
        main.DemoHover(false);
        studio.DemoElapsed = TimeSpan.Zero;
        studio.DemoSet(StudioState.Recording);
        double rec = 0;
        Step(80, _ => { rec += 1 / Fps * 9; studio.DemoElapsed = TimeSpan.FromSeconds(rec); studio.DemoBytes = (long)(rec * 2.6e6); });
        studio.DemoSet(StudioState.Paused);
        Step(40);
        studio.DemoSet(StudioState.Recording);
        Step(50, _ => { rec += 1 / Fps * 9; studio.DemoElapsed = TimeSpan.FromSeconds(rec); studio.DemoBytes = (long)(rec * 2.6e6); });
        studio.DemoSet(StudioState.Finishing);
        Step(12);
        var thumb = Path.Combine(_dir, "frames", "take-thumb.jpg");
        SaveJpeg(Game(t, 480, 270), thumb);
        studio.DemoSet(StudioState.Idle);
        studio.DemoSaved(new Take("C:\\Videos\\SweaxIris\\take.mp4", DateTime.Now, rec, (long)(rec * 2.6e6), 2560, 1440, 60, "NVIDIA NVENC · H.264", thumb, 0));
        main.BuildTakes();
        Step(50);
        main.Close();
        await Task.CompletedTask;
    }

    /// <summary>The region picker over a drawn desktop: crosshair, drag, adjust, Enter, countdown, the frame and bar.</summary>
    static async Task FilmRegion()
    {
        var (w, h) = (1280.0, 720.0);
        var film = new Film("region");
        film.Start();
        var desk = new DesktopScene();
        double t = 0;
        var cursor = new Point(w * 0.3, h * 0.45);
        Rect sel = Rect.Empty;
        string phase = "hover";
        for (int f = 0; f < 210; f++)
        {
            t += 1 / Fps;
            desk.Time = t;
            var deskBmp = Snap.Render(desk, w, h, 1);
            var chart = desk.ChartWindow;
            var a = new Point(chart.X + 14, chart.Y + 42);
            var b = new Point(chart.Right - 14, chart.Bottom - 12);
            if (f < 40) cursor = Lerp(new Point(w * 0.3, h * 0.45), a, Ease(f / 40.0));
            else if (f < 46) { phase = "draw"; sel = new Rect(a, a); }
            else if (f < 96) { cursor = Lerp(a, b, Ease((f - 46) / 50.0)); sel = new Rect(a, cursor); }
            else if (f < 130) phase = "adjust";
            var g = new Grid { Width = w, Height = h };
            g.Children.Add(new Image { Source = deskBmp });
            if (f < 130)
            {
                g.Children.Add(RegionSelector.DemoVisual(w, h, sel, phase, cursor, phase == "hover" && f > 20 ? chart : null));
                g.Children.Add(Pointer(cursor, phase));
            }
            else g.Children.Add(DemoRecording(w, h, sel, f - 130));
            Springs.Step(1 / Fps);
            film.Frame(Stage(g, w, h, 1, 24));
        }
        await Task.CompletedTask;
    }

    static Studio? _barStudio;
    static Iris? _countIris;

    /// <summary>After Enter in the region film: the countdown over the region, then the frame and the bar.</summary>
    static FrameworkElement DemoRecording(double w, double h, Rect sel, int f)
    {
        var c = new Canvas { Width = w, Height = h };
        if (f < 54)
        {
            int n = 3 - f / 18;
            _countIris ??= new Iris { Width = 150, Height = 150, Aperture = 0.5, Core = (Color)Application.Current.Resources["TallyColor"] };
            if (f % 18 == 0) { Springs.To(_countIris, Iris.ApertureProperty, 0.62, 260, 0.62, 0.08); Springs.To(_countIris, Iris.TurnProperty, _countIris.Turn + 51.4, 200, 0.85); }
            var g = new Grid { Width = 180, Height = 180 };
            g.Children.Add(new Border { CornerRadius = new CornerRadius(90), Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) });
            g.Children.Add(new Image { Source = Snap.Render(_countIris, 150, 150, 1), Width = 150, Height = 150 });
            g.Children.Add(new TextBlock { Text = n.ToString(), FontSize = 50, Foreground = Brushes.White, FontFamily = (FontFamily)Application.Current.FindResource("PlexSemi"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            Canvas.SetLeft(g, sel.Left + sel.Width / 2 - 90);
            Canvas.SetTop(g, sel.Top + sel.Height / 2 - 90);
            c.Children.Add(g);
            return c;
        }
        // the frame just outside the region, marching
        var tally = (Color)Application.Current.Resources["TallyColor"];
        var frame = new System.Windows.Shapes.Rectangle { Width = sel.Width + 6, Height = sel.Height + 6, Stroke = new SolidColorBrush(tally), StrokeThickness = 2, StrokeDashArray = [6, 4], StrokeDashOffset = -f * 0.6 };
        Canvas.SetLeft(frame, sel.Left - 3);
        Canvas.SetTop(frame, sel.Top - 3);
        c.Children.Add(frame);
        // the recording bar under it
        if (_barStudio == null)
        {
            _barStudio = NewStudio();
            _barStudio.DemoSet(CaptureTarget.Screen(IntPtr.Zero));
            _barStudio.DemoSystemPeak = () => 0.45f;
            _barStudio.DemoMicPeak = () => 0.2f;
            _barStudio.DemoSet(StudioState.Recording);
        }
        _barStudio.DemoElapsed = TimeSpan.FromSeconds((f - 54) / Fps);
        var bar = new ControllerWindow(_barStudio) { Left = -30000, Top = -30000 };
        bar.Show();
        Settle(f == 54 ? 30 : 1);
        bar.DemoTick();
        var content = (FrameworkElement)bar.Content;
        var img = new Image { Source = Snap.Render(content, content.ActualWidth, content.ActualHeight, 1.5), Width = content.ActualWidth, Height = content.ActualHeight };
        bar.Close();
        Canvas.SetLeft(img, sel.Left + sel.Width / 2 - content.ActualWidth / 2);
        Canvas.SetTop(img, Math.Min(h - content.ActualHeight, sel.Bottom + 4));
        c.Children.Add(img);
        return c;
    }

    static FrameworkElement Pointer(Point p, string phase)
    {
        var c = new Canvas { IsHitTestVisible = false };
        var path = new System.Windows.Shapes.Path
        {
            Data = phase == "hover"
                ? Geometry.Parse("M0,0 L0,17 L4.5,12.8 L7.6,19.6 L10.4,18.4 L7.4,11.8 L13.2,11.6 Z")
                : Geometry.Parse("M-9,0 H9 M0,-9 V9"),
            Fill = phase == "hover" ? Brushes.White : null,
            Stroke = phase == "hover" ? Brushes.Black : Brushes.White,
            StrokeThickness = phase == "hover" ? 1.1 : 1.6,
        };
        Canvas.SetLeft(path, p.X);
        Canvas.SetTop(path, p.Y);
        c.Children.Add(path);
        return c;
    }

    static Point Lerp(Point a, Point b, double k) => new(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);
    static double Ease(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

    static void SaveJpeg(BitmapSource b, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new JpegBitmapEncoder { QualityLevel = 90 };
        enc.Frames.Add(BitmapFrame.Create(b));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
