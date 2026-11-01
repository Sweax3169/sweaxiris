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
        _dir = Path.GetFullPath(Arg("--shots") ?? Path.Combine(Path.GetTempPath(), "sweaxiris-shots"));
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
                if (Want("library")) LibraryStill();
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
        // made-up pictures for every tile of the source picker
        var pics = new Dictionary<string, BitmapSource>();
        MainWindow.DemoPicture = key =>
        {
            if (pics.TryGetValue(key, out var b)) return b;
            if (key.StartsWith("mon:")) key = key.EndsWith("DISPLAY2") ? "d2" : key.EndsWith("DISPLAY3") ? "d3" : "d1";
            b = key switch
            {
                "d1" => Game(3.2, 640, 360),
                "d2" => Snap.Render(new DesktopScene { Time = 1 }, 1280, 720, 0.5),
                "d3" => Snap.Render(new DesktopScene { Time = 4 }, 540, 960, 0.6),
                "win:1" => Game(6.5, 640, 360),
                "win:2" => Crop(Snap.Render(new DesktopScene { Time = 2 }, 1280, 720, 1), 0.38, 0.2, 0.56, 0.64),
                "win:3" => Crop(Snap.Render(new DesktopScene { Time = 2 }, 1280, 720, 1), 0.04, 0.07, 0.46, 0.6),
                "win:4" => Snap.Render(new GameScene { Time = 9 }, 480, 300, 1),
                _ when key.StartsWith("reg:") => Crop(Snap.Render(new DesktopScene { Time = 3 }, 1280, 720, 1), 0.38, 0.2, 0.56, 0.64),
                _ => null,
            };
            if (b != null) pics[key] = b;
            return b;
        };
    }

    static BitmapSource Crop(BitmapSource b, double x, double y, double w, double h)
    {
        var r = new Int32Rect((int)(b.PixelWidth * x), (int)(b.PixelHeight * y), (int)(b.PixelWidth * w), (int)(b.PixelHeight * h));
        var c = new CroppedBitmap(b, r);
        c.Freeze();
        return c;
    }

    static Studio NewStudio()
    {
        var s = new Settings { Monitor = @"\\.\DISPLAY1", Microphone = true, Fps = 60, Resolution = 1440, Quality = 0.72, KeyRecord = "Ctrl+Alt+R", KeyRegion = "Ctrl+Alt+A", KeyPause = "Pause", KeyMic = "Ctrl+Alt+M", LastRegion = [972, 288, 2406, 1209], OutputFolder = Path.Combine("D:" + Path.DirectorySeparatorChar, "Videos", "SweaxIris") };
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
        foreach (var mode in new[] { "window", "region" })
        {
            main.DemoMode(mode);
            Settle(40);
            Snap.Save(Stage((FrameworkElement)main.Content, main.ActualWidth, main.ActualHeight), Out("main-" + mode));
        }
        main.DemoMode("screen");
        Settle(30);

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

    static void LibraryStill()
    {
        var studio = NewStudio();
        var scenes = new Func<int, BitmapSource>[]
        {
            i => Game(2 + i * 3.1, 480, 270),
            i => Crop(Snap.Render(new DesktopScene { Time = i }, 1280, 720, 1), 0.38, 0.2, 0.56, 0.64),
            i => Snap.Render(new DesktopScene { Time = i }, 960, 540, 0.5),
        };
        var rng = new Random(7);
        var when = DateTime.Now;
        var takes = new List<Take>();
        for (int i = 0; i < 9; i++)
        {
            var thumb = Path.Combine(_dir, "frames", $"lib-{i}-{_suffix}.jpg");
            SaveJpeg(scenes[i % 3](i), thumb);
            double secs = 20 + rng.Next(600);
            when = when.AddMinutes(-(i == 0 ? 2 : 37 + rng.Next(400)));
            takes.Add(new Take(Path.Combine("C:", "Videos", "SweaxIris", $"take-{i}.mp4"), when, secs, (long)(secs * (1.5e6 + rng.NextDouble() * 2e6)), i % 3 == 1 ? 1434 : 2560, i % 3 == 1 ? 921 : 1440, i % 2 == 0 ? 60 : 144, "NVIDIA NVENC · H.264", thumb, 0));
        }
        for (int i = takes.Count - 1; i >= 0; i--) studio.DemoSaved(takes[i]);
        var main = OpenMain(studio);
        main.DemoPage("library");
        Settle(50);
        Snap.Save(Stage((FrameworkElement)main.Content, main.ActualWidth, main.ActualHeight), Out("library"));
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
        var overlay = RegionSelector.DemoVisual(w, h, sel, "adjust", new Point(sel.Right - 40, sel.Bottom - 30), null, deskBmp);
        var g = new Grid { Width = w, Height = h };
        g.Children.Add(new Image { Source = deskBmp });
        g.Children.Add(overlay);
        Snap.Save(Stage(g, w, h, 1.25, 30), Out("select"));
        // choosing: the frame on a window, the loupe at the pointer
        var cur = new Point(desk.ChartWindow.X + 222, desk.ChartWindow.Y + 354);
        var hover = RegionSelector.DemoVisual(w, h, Rect.Empty, "hover", cur, desk.ChartWindow, deskBmp);
        var g2 = new Grid { Width = w, Height = h };
        g2.Children.Add(new Image { Source = deskBmp });
        g2.Children.Add(hover);
        g2.Children.Add(Pointer(cur, "hover"));
        Snap.Save(Stage(g2, w, h, 1.25, 30), Out("select-hover"));
    }

    // ── films (PNG frames) ──────────────────────────────────────────────────

    sealed class Film(string name)
    {
        readonly string _dir = Path.Combine(Shots._dir, "frames", $"{name}-{Shots._suffix}");
        int _n;
        public void Start() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); Directory.CreateDirectory(_dir); }
        public void Frame(BitmapSource b) => Snap.Save(b, Path.Combine(_dir, $"{_n++:00000}.png"));
    }

    /// <summary>
    /// The studio at work: Screen, then Window, a tile hovered and chosen, back to Screen, hover the iris, 3-2-1,
    /// the picker gives way to the program picture, pause, resume, stop, and the take lands in Takes.
    /// </summary>
    static async Task FilmRecord()
    {
        var studio = NewStudio();
        var main = OpenMain(studio);
        var film = new Film("record");
        film.Start();
        double t = 0;
        bool live = false;
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
                if (live) main.ShowFrame(Game(t, 640, 360));
                Springs.Step(1 / Fps);
                Pump();
                main.Tick();
                film.Frame(Stage((FrameworkElement)main.Content, main.ActualWidth, main.ActualHeight, 1.25, 28));
            }
        }
        Step(24);
        main.DemoMode("window");
        Step(26);
        main.DemoTile(1, "hover");
        Step(14);
        main.DemoTile(1, "pick");
        Step(22);
        main.DemoTile(1, "leave");
        main.DemoMode("screen");
        Step(26);
        main.DemoTile(1, "hover");
        Step(10);
        main.DemoTile(1, "leave");
        main.DemoTile(2, "hover");
        Step(12);
        main.DemoTile(2, "pick");
        Step(18);
        main.DemoTile(2, "leave");
        main.DemoHover(true);
        Step(14);
        studio.DemoSet(CaptureTarget.Screen(IntPtr.Zero));
        studio.DemoSet(StudioState.Arming);
        for (int n = 3; n >= 1; n--) { main.DemoCount(n); Step(22); }
        main.DemoCount(0);
        main.DemoHover(false);
        studio.DemoElapsed = TimeSpan.Zero;
        live = true;
        studio.DemoSet(StudioState.Recording);
        double rec = 0;
        Step(80, _ => { rec += 1 / Fps * 9; studio.DemoElapsed = TimeSpan.FromSeconds(rec); studio.DemoBytes = (long)(rec * 2.6e6); });
        studio.DemoSet(StudioState.Paused);
        Step(36);
        studio.DemoSet(StudioState.Recording);
        Step(44, _ => { rec += 1 / Fps * 9; studio.DemoElapsed = TimeSpan.FromSeconds(rec); studio.DemoBytes = (long)(rec * 2.6e6); });
        studio.DemoSet(StudioState.Finishing);
        Step(12);
        live = false;
        var thumb = Path.Combine(_dir, "frames", $"take-thumb-{_suffix}.jpg"); // one per run, the four runs go in parallel
        SaveJpeg(Game(t, 480, 270), thumb);
        studio.DemoSet(StudioState.Idle);
        studio.DemoSaved(new Take(Path.Combine("C:", "Videos", "SweaxIris", "take.mp4"), DateTime.Now, rec, (long)(rec * 2.6e6), 2560, 1440, 60, "NVIDIA NVENC · H.264", thumb, 0));
        main.BuildTakes();
        Step(30);
        main.DemoPage("library");
        Step(46);
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
                g.Children.Add(RegionSelector.DemoVisual(w, h, sel, phase, cursor, phase == "hover" && f > 20 ? chart : null, deskBmp));
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
            g.Children.Add(new TextBlock { Text = n.ToString(), FontSize = 50, Foreground = Brushes.White, FontFamily = (FontFamily)Application.Current.FindResource("Plex"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
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
