using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SweaxIris.Audio;
using SweaxIris.Capture;
using SweaxIris.Core;
using SweaxIris.Native;
using SweaxIris.Recording;

namespace SweaxIris.UI;

/// <summary>
/// The studio window: a live viewfinder of what will be recorded, the iris record button, the source,
/// sound and video choices beside it and the takes underneath. Settings slide in over all of it.
/// </summary>
public partial class MainWindow : Window
{
    readonly Studio _studio;
    Settings S => _studio.S;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _windowsRefresh = new() { Interval = TimeSpan.FromSeconds(3) };
    IdlePreview? _preview;
    CaptureStream? _sysMonitor, _micMonitor;
    SettingsView? _settings;
    bool _hover, _ready;
    double _breath;

    /// <summary>Set by the app: what the record button and the region button do.</summary>
    public Action? RecordRequested, RegionRequested, PauseRequested, MicRequested;

    /// <summary>For demo pictures: a picture source instead of the screen.</summary>
    public Func<Gpu, IVideoSource>? TestPreview { get; set; }

    public MainWindow(Studio studio)
    {
        _studio = studio;
        InitializeComponent();
        Tagline.Text = Loc.T("app.tagline");
        DrawRegionText.Text = Loc.T("src.draw");

        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();
        SettingsButton.Click += (_, _) => ShowSettings(true);
        OpenFolder.Click += (_, _) => { Directory.CreateDirectory(S.Folder); Core.Shell.Open(S.Folder); };
        DrawRegion.Click += (_, _) => RegionRequested?.Invoke();
        PauseButton.Click += (_, _) => PauseRequested?.Invoke();
        MicButton.Click += (_, _) => MicRequested?.Invoke();

        // the iris: hover opens it a little, pressing records
        RecordHost.MouseEnter += (_, _) => { _hover = true; ApplyIris(); };
        RecordHost.MouseLeave += (_, _) => { _hover = false; ApplyIris(); };
        RecordHost.MouseLeftButtonDown += (_, e) => { if (_studio.State == StudioState.Idle) Springs.To(RecordIris, Iris.ApertureProperty, 0.08, 700, 0.9); e.Handled = true; };
        RecordHost.MouseLeftButtonUp += (_, _) => RecordRequested?.Invoke();

        SourceSeg.SetItems([("screen", Loc.T("src.screen")), ("window", Loc.T("src.window")), ("region", Loc.T("src.region"))]);
        SourceSeg.Value = S.Source;
        SourceSeg.Changed += v => { S.Source = (string)v; S.Save(); ShowSource(); RestartPreview(); Refresh(); };

        SysToggle.IsChecked = S.SystemAudio;
        MicToggle.IsChecked = S.Microphone;
        SysToggle.Click += (_, _) => { S.SystemAudio = SysToggle.IsChecked == true; S.Save(); Meters(); };
        MicToggle.Click += (_, _) => { S.Microphone = MicToggle.IsChecked == true; S.Save(); Meters(); };
        SysVolume.Value = S.SystemVolume;
        MicVolume.Value = S.MicVolume;
        SysVolume.ValueChanged += (_, e) => { S.SystemVolume = e.NewValue; _studio.SetVolumes(); };
        MicVolume.ValueChanged += (_, e) => { S.MicVolume = e.NewValue; _studio.SetVolumes(); };
        SysVolume.LostMouseCapture += (_, _) => S.Save();
        MicVolume.LostMouseCapture += (_, _) => S.Save();
        SysMeter.Source = () => _studio.SystemPeak is >= 0 and var p ? p : (_sysMonitor?.Peak ?? 0) * (float)S.SystemVolume;
        MicMeter.Source = () => _studio.MicPeak is >= 0 and var p ? p : (_micMonitor?.Peak ?? 0) * (float)S.MicVolume * (_studio.MicMuted ? 0 : 1);

        ResCombo.ItemsSource = new[] { 0, 2160, 1440, 1080, 720, 480 }.Select(h => new ComboBoxItem { Content = h == 0 ? Loc.T("video.native") : $"{h}p", Tag = h }).ToList();
        ResCombo.SelectedItem = ((List<ComboBoxItem>)ResCombo.ItemsSource).FirstOrDefault(i => (int)i.Tag == S.Resolution) ?? ((List<ComboBoxItem>)ResCombo.ItemsSource)[0];
        ResCombo.SelectionChanged += (_, _) => { if (ResCombo.SelectedItem is ComboBoxItem i) { S.Resolution = (int)i.Tag; S.Save(); Refresh(); } };
        FpsCombo.ItemsSource = new[] { 24, 30, 48, 50, 60, 90, 120, 144, 165, 240 }.Select(f => new ComboBoxItem { Content = Loc.F("video.fps", f), Tag = f }).ToList();
        FpsCombo.SelectedItem = ((List<ComboBoxItem>)FpsCombo.ItemsSource).FirstOrDefault(i => (int)i.Tag == S.Fps) ?? ((List<ComboBoxItem>)FpsCombo.ItemsSource)[4];
        FpsCombo.SelectionChanged += (_, _) => { if (FpsCombo.SelectedItem is ComboBoxItem i) { S.Fps = (int)i.Tag; S.Save(); Refresh(); } };
        QualitySeg.SetItems([(0.30, Loc.T("q.small")), (0.55, Loc.T("q.balanced")), (0.72, Loc.T("q.high")), (0.92, Loc.T("q.max"))]);
        QualitySeg.Value = NearestQuality(S.Quality);
        QualitySeg.Changed += v => { S.Quality = (double)v; S.ConstantQuality = true; S.Save(); Refresh(); };

        _studio.StateChanged += OnState;
        _studio.CountdownTick += OnCount;
        _studio.RecordingPreview += bmp => { if (_studio.State is StudioState.Recording or StudioState.Paused) ShowFrame(bmp); };
        _studio.EncodersReady += Refresh;
        _studio.Saved += _ => BuildTakes();

        _clock.Tick += (_, _) => { if (!Studio.Demo) Tick(); };
        _windowsRefresh.Tick += (_, _) => { if (WindowPane.IsVisible) FillWindows(); };
        Preview.SizeChanged += (_, _) => PlaceBrackets();
        Finder.SizeChanged += (_, _) => PlaceBrackets();
        MonitorMap.SizeChanged += (_, _) => DrawMonitors();
        IsVisibleChanged += (_, _) => { RestartPreview(); Meters(); };
        StateChanged += (_, _) => RestartPreview();
        Theme.Changed += () => { DrawMonitors(); ApplyIris(); };
        SourceInitialized += (_, _) => ApplyCaptureExclusion();
        Loaded += (_, _) =>
        {
            _ready = true;
            ShowSource();
            Refresh();
            BuildTakes();
            ApplyIris(true);
            _clock.Start();
        };
        Closing += OnClosing;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _settings != null && SettingsHost.Visibility == Visibility.Visible) { ShowSettings(false); e.Handled = true; } };
    }

    public void ApplyCaptureExclusion()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) Win32.SetWindowDisplayAffinity(h, S.HideFromCapture ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE);
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        // the close button tucks SweaxIris into the tray; the shortcuts keep working
        if (App.Quitting) return;
        e.Cancel = true;
        Hide();
    }

    static double NearestQuality(double q) => new[] { 0.30, 0.55, 0.72, 0.92 }.OrderBy(x => Math.Abs(x - q)).First();

    // ── source ──────────────────────────────────────────────────────────────

    void ShowSource()
    {
        ScreenPane.Visibility = S.Source == "screen" ? Visibility.Visible : Visibility.Collapsed;
        WindowPane.Visibility = S.Source == "window" ? Visibility.Visible : Visibility.Collapsed;
        RegionPane.Visibility = S.Source == "region" ? Visibility.Visible : Visibility.Collapsed;
        SourceTitle.Text = Loc.T(S.Source switch { "window" => "src.window.hint", "region" => "src.region.hint", _ => "src.screen.hint" });
        if (S.Source == "window") { FillWindows(); _windowsRefresh.Start(); } else _windowsRefresh.Stop();
        if (S.Source == "screen") DrawMonitors();
        if (S.Source == "region") ShowRegion();
        RegionKeys.Content = KeyCaps.Build(Hotkey.Parse(S.KeyRegion), 11);
        SourceBody.Opacity = 0;
        Springs.To(SourceBody, OpacityProperty, 1, 260, 1);
    }

    void DrawMonitors()
    {
        if (!_ready || MonitorMap.ActualWidth <= 0) return;
        MonitorMap.Children.Clear();
        var mons = Studio.Monitors();
        if (mons.Count == 0) return;
        int minX = mons.Min(m => m.Bounds.Left), minY = mons.Min(m => m.Bounds.Top);
        int maxX = mons.Max(m => m.Bounds.Right), maxY = mons.Max(m => m.Bounds.Bottom);
        double W = MonitorMap.ActualWidth, H = MonitorMap.Height;
        double s = Math.Min(W / (maxX - minX), H / (maxY - minY));
        double ox = (W - (maxX - minX) * s) / 2, oy = (H - (maxY - minY) * s) / 2;
        var chosen = string.IsNullOrEmpty(S.Monitor) ? null : S.Monitor;
        foreach (var m in mons)
        {
            bool on = chosen != null && m.Device.Equals(chosen, StringComparison.OrdinalIgnoreCase);
            var b = new Border
            {
                Width = Math.Max(10, m.Bounds.Width * s - 4), Height = Math.Max(10, m.Bounds.Height * s - 4), CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(on ? 2 : 1), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = m.Number.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("PlexSemi"), FontSize = 13 },
                ToolTip = $"{m.Name} · {m.Bounds.Width} × {m.Bounds.Height}{(m.Hz > 0 ? $" · {Math.Round(m.Hz)} Hz" : "")}",
            };
            b.SetResourceReference(Border.BackgroundProperty, on ? "AccentSoft" : "Raised");
            b.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "LineStrong");
            ((TextBlock)b.Child).SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "TextDim");
            Canvas.SetLeft(b, ox + (m.Bounds.Left - minX) * s + 2);
            Canvas.SetTop(b, oy + (m.Bounds.Top - minY) * s + 2);
            var dev = m.Device;
            b.MouseLeftButtonUp += (_, _) =>
            {
                S.Monitor = S.Monitor.Equals(dev, StringComparison.OrdinalIgnoreCase) ? "" : dev;
                S.Save();
                DrawMonitors();
                RestartPreview();
                Refresh();
            };
            Press.SetEnabled(b, true);
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            MonitorMap.Children.Add(b);
        }
        var cur = _studio.ChosenMonitor();
        MonitorLabel.Text = chosen == null
            ? $"{Loc.T("src.mouse")} · {cur?.Name}"
            : $"{cur?.Name} · {cur?.Bounds.Width} × {cur?.Bounds.Height}{(cur?.Hz > 0 ? $" · {Math.Round(cur.Hz)} Hz" : "")}";
    }

    void FillWindows()
    {
        WindowList.Children.Clear();
        if (Studio.DemoWindows is { } demo)
        {
            bool firstRow = true;
            foreach (var (title, app) in demo)
            {
                var row = new RadioButton { Style = (Style)FindResource("Row"), GroupName = "win", IsChecked = firstRow, Padding = new Thickness(8, 6, 8, 6) };
                firstRow = false;
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
                var sub = new TextBlock { Text = app, FontSize = 11 };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
                sp.Children.Add(sub);
                row.Content = sp;
                WindowList.Children.Add(row);
            }
            return;
        }
        var wins = Win32.RealWindows();
        foreach (var h in wins.Take(24))
        {
            Win32.GetWindowThreadProcessId(h, out uint pid);
            string exe = Path.GetFileNameWithoutExtension(Win32.GetProcessPath(pid) ?? "");
            var row = new RadioButton { Style = (Style)FindResource("Row"), GroupName = "win", IsChecked = h == _studio.ChosenWindow, Padding = new Thickness(8, 6, 8, 6) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = new Image { Width = 18, Height = 18, Source = AppIcons.For(h, pid), Margin = new Thickness(0, 0, 9, 0) };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = Win32.GetWindowTitle(h), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 });
            var sub = new TextBlock { Text = exe, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
            text.Children.Add(sub);
            Grid.SetColumn(text, 1);
            g.Children.Add(icon);
            g.Children.Add(text);
            row.Content = g;
            var hwnd = h;
            row.Checked += (_, _) => { _studio.ChosenWindow = hwnd; RestartPreview(); Refresh(); };
            WindowList.Children.Add(row);
        }
    }

    void ShowRegion()
    {
        if (_studio.LastRegion is { } r)
        {
            RegionSize.Text = $"{r.Width} × {r.Height}";
            var mon = Studio.Monitors().FirstOrDefault(m => m.Handle == Win32.MonitorFromRect(ref r, Win32.MONITOR_DEFAULTTONEAREST));
            RegionWhere.Text = $"{r.Left}, {r.Top} · {mon?.Name}";
            DrawRegionText.Text = Loc.T("src.redraw");
        }
        else
        {
            RegionSize.Text = Loc.T("src.noregion");
            RegionWhere.Text = Loc.T("sel.hint");
            DrawRegionText.Text = Loc.T("src.draw");
        }
    }

    public void RegionChanged()
    {
        SourceSeg.Value = S.Source;
        ShowSource();
        RestartPreview();
        Refresh();
    }

    // ── live picture ────────────────────────────────────────────────────────

    public void RestartPreview()
    {
        _preview?.Dispose();
        _preview = null;
        if (Studio.Demo) return;
        bool recording = _studio.State is StudioState.Recording or StudioState.Paused or StudioState.Finishing;
        if (!IsVisible || WindowState == WindowState.Minimized || recording) { if (!recording) ClearFrame(); return; }
        var t = _studio.CurrentTarget();
        if (t == null && TestPreview == null) { ClearFrame(); return; }
        try
        {
            _preview = new IdlePreview(t ?? CaptureTarget.Screen(IntPtr.Zero), S.Cursor, TestPreview);
            _preview.Frame += (px, w, h) => Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                if (_preview == null || _studio.State is StudioState.Recording or StudioState.Paused) return;
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
                bmp.Freeze();
                ShowFrame(bmp);
            });
        }
        catch (Exception ex)
        {
            Paths.Log("preview start: " + ex.Message);
            ClearFrame();
        }
    }

    public void ShowFrame(BitmapSource bmp)
    {
        bool first = Preview.Source == null;
        Preview.Source = bmp;
        FinderEmpty.Visibility = Visibility.Collapsed;
        if (first) { Preview.Opacity = 0; Springs.To(Preview, OpacityProperty, 1, 200, 1); PlaceBrackets(); }
    }

    void ClearFrame()
    {
        Preview.Source = null;
        FinderEmpty.Visibility = Visibility.Visible;
        FinderEmptyText.Text = S.Source == "window" ? Loc.T("src.nowindow") : S.Source == "region" && _studio.LastRegion == null ? Loc.T("src.noregion") : "";
        PlaceBrackets();
    }

    /// <summary>Viewfinder corner marks around the picture itself.</summary>
    void PlaceBrackets()
    {
        double fw = Finder.ActualWidth - 2, fh = Finder.ActualHeight - 2;
        if (fw <= 0) return;
        Rect r;
        if (Preview.Source is BitmapSource b && b.PixelWidth > 0)
        {
            double aw = fw - 36, ah = fh - 36, s = Math.Min(aw / b.PixelWidth, ah / b.PixelHeight);
            double w = b.PixelWidth * s, h = b.PixelHeight * s;
            r = new Rect((fw - w) / 2 - 6, (fh - h) / 2 - 6, w + 12, h + 12);
        }
        else r = new Rect(12, 12, fw - 24, fh - 24);
        double k = Math.Min(22, Math.Min(r.Width, r.Height) / 4);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            void L(Point a, Point m, Point z) { c.BeginFigure(a, false, false); c.LineTo(m, true, true); c.LineTo(z, true, true); }
            L(new(r.Left, r.Top + k), new(r.Left, r.Top), new(r.Left + k, r.Top));
            L(new(r.Right - k, r.Top), new(r.Right, r.Top), new(r.Right, r.Top + k));
            L(new(r.Right, r.Bottom - k), new(r.Right, r.Bottom), new(r.Right - k, r.Bottom));
            L(new(r.Left + k, r.Bottom), new(r.Left, r.Bottom), new(r.Left, r.Bottom - k));
        }
        g.Freeze();
        Brackets.Data = g;
    }

    // ── meters while idle ───────────────────────────────────────────────────

    void Meters()
    {
        bool idle = IsVisible && _studio.Recorder == null && !Studio.Demo;
        bool sys = idle && S.SystemAudio, mic = idle && S.Microphone;
        if (sys && _sysMonitor == null) { _sysMonitor = new CaptureStream(CaptureStream.Kind.System, S.SystemDevice); _sysMonitor.Start(); }
        if (!sys && _sysMonitor != null) { var m = _sysMonitor; _sysMonitor = null; Task.Run(m.Dispose); }
        if (mic && _micMonitor == null) { _micMonitor = new CaptureStream(CaptureStream.Kind.Microphone, S.MicDevice); _micMonitor.Start(); }
        if (!mic && _micMonitor != null) { var m = _micMonitor; _micMonitor = null; Task.Run(m.Dispose); }
        SysMeter.Muted = !S.SystemAudio;
        MicMeter.Muted = !S.Microphone || _studio.MicMuted;
    }

    // ── state ───────────────────────────────────────────────────────────────

    /// <summary>Re-reads settings into the summary lines (after any change).</summary>
    public void Refresh()
    {
        if (!_ready) return;
        var t = _studio.CurrentTarget();
        int sw = 0, sh = 0;
        string what = "";
        if (Studio.Demo && DemoSource is { } ds) { t = null; (what, sw, sh) = ds; }
        else if (t != null)
        {
            if (t.Kind == SourceKind.Region) { sw = t.Region.Width; sh = t.Region.Height; what = Loc.T("src.region"); }
            else if (t.Kind == SourceKind.Window) { var f = Win32.FrameOf(t.Window); sw = f.Width; sh = f.Height; what = Win32.GetWindowTitle(t.Window); }
            else if (_studio.ChosenMonitor() is { } m) { sw = m.Bounds.Width; sh = m.Bounds.Height; what = m.Name; }
        }
        SourceChip.Text = Studio.Demo && DemoSource != null ? $"{what} · {sw} × {sh}" : t == null ? Loc.T(S.Source == "window" ? "src.nowindow" : "src.noregion") : $"{Trim(what, 34)} · {sw} × {sh}";
        if (sw > 0)
        {
            double sc = S.Resolution > 0 && S.Resolution < sh ? (double)S.Resolution / sh : 1;
            int ow = (int)Math.Round(sw * sc / 2) * 2, oh = (int)Math.Round(sh * sc / 2) * 2;
            OutSize.Text = $"{ow} × {oh}";
        }
        else OutSize.Text = "—";
        OutLine.Text = $"{Loc.F("video.fps", S.Fps)} · {Loc.T(S.ConstantQuality ? QualityKey(S.Quality) : "set.rate.cbr")}{(S.ConstantQuality ? "" : $" {S.BitrateKbps / 1000} Mbps")}";
        var enc = _studio.PickEncoder(t);
        OutLine2.Text = $"{enc} · {S.Container.ToUpperInvariant()}";
        EncoderLine.Text = _studio.Encoders == null ? Loc.T("set.encoder.checking") : $"{enc} · {S.Container.ToUpperInvariant()}";
        SysDeviceText.Text = DeviceName(EDataFlow.Render, S.SystemDevice) + (S.AppAudioOnly && S.Source == "window" ? " · " + Loc.T("sound.apponly") : "");
        MicDeviceText.Text = DeviceName(EDataFlow.Capture, S.MicDevice);
        RecordKeys.Content = KeyCaps.Build(Hotkey.Parse(S.KeyRecord), 11);
        if (S.Source == "region") ShowRegion();
        UpdateDeck();
    }

    static string Trim(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;
    static string QualityKey(double q) => NearestQuality(q) switch { 0.30 => "q.small", 0.55 => "q.balanced", 0.72 => "q.high", _ => "q.max" };

    static readonly Dictionary<string, string> _deviceNames = [];
    public static string? DemoSystemDevice, DemoMicDevice;
    public static (string What, int W, int H)? DemoSource;

    static string DeviceName(EDataFlow flow, string id)
    {
        if (Studio.Demo) return (flow == EDataFlow.Render ? DemoSystemDevice : DemoMicDevice) ?? "";
        string key = flow + id;
        if (_deviceNames.TryGetValue(key, out var n)) return n;
        var list = AudioDevices.List(flow);
        var d = string.IsNullOrEmpty(id) ? list.FirstOrDefault(x => x.IsDefault) : list.FirstOrDefault(x => x.Id == id);
        n = d == null ? Loc.T("sound.default") : string.IsNullOrEmpty(id) ? $"{d.Name}" : d.Name;
        return _deviceNames[key] = n;
    }

    public static void ForgetDeviceNames() => _deviceNames.Clear();

    void OnState()
    {
        UpdateDeck();
        Meters();
        if (_studio.State == StudioState.Idle) RestartPreview();
        if (_studio.State == StudioState.Recording && _preview != null) { _preview.Dispose(); _preview = null; }
    }

    void OnCount(int n)
    {
        if (n <= 0) { Springs.To(CountText, OpacityProperty, 0, 400, 1); return; }
        CountText.Text = n.ToString();
        CountText.Opacity = 1;
        // each second the lens stops down and opens again
        Springs.To(RecordIris, Iris.ApertureProperty, 0.55, 260, 0.6, 0.05);
        Springs.To(RecordIris, Iris.TurnProperty, RecordIris.Turn + 51.4, 220, 0.8);
    }

    void UpdateDeck()
    {
        var st = _studio.State;
        bool live = st is StudioState.Recording or StudioState.Paused;
        Fade(PauseButton, live);
        Fade(MicButton, live && S.Microphone);
        PauseIco.Data = (Geometry)FindResource(st == StudioState.Paused ? "IcoPlay" : "IcoPause");
        PauseIco.FillShape = st == StudioState.Paused;
        PauseButton.ToolTip = Loc.T(st == StudioState.Paused ? "resume" : "pause");
        MicIco.Data = (Geometry)FindResource(_studio.MicMuted ? "IcoMicOff" : "IcoMic");
        MicRowIco.Data = MicIco.Data;
        MicButton.SetResourceReference(ForegroundProperty, _studio.MicMuted ? "Tally" : "Text");
        StatusText.Text = st switch
        {
            StudioState.Arming => Loc.T("arming"),
            StudioState.Recording => Loc.T("recording"),
            StudioState.Paused => Loc.T("paused"),
            StudioState.Finishing => Loc.T("finishing"),
            _ => Loc.T("record"),
        };
        RecordKeys.Visibility = st == StudioState.Idle ? Visibility.Visible : Visibility.Collapsed;
        RecChip.Visibility = FinderRec.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        Timecode.SetResourceReference(TextBlock.ForegroundProperty, live ? "Text" : "TextFaint");
        if (st == StudioState.Idle) Timecode.Text = "00:00:00";
        SourceSeg.IsEnabled = st == StudioState.Idle;
        ApplyIris();
        Meters();
    }

    static void Fade(FrameworkElement el, bool on)
    {
        el.IsHitTestVisible = on;
        Springs.To(el, OpacityProperty, on ? 1 : 0, 300, 0.9);
        var s = el.RenderTransform as ScaleTransform;
        if (s == null || s.IsFrozen) { s = new ScaleTransform(1, 1); el.RenderTransform = s; el.RenderTransformOrigin = new Point(0.5, 0.5); }
        Springs.To(s, ScaleTransform.ScaleXProperty, on ? 1 : 0.6, 380, 0.62);
        Springs.To(s, ScaleTransform.ScaleYProperty, on ? 1 : 0.6, 380, 0.62);
    }

    /// <summary>Springs the iris to the look of the current state.</summary>
    void ApplyIris(bool snap = false)
    {
        if (!_ready || Application.Current.Resources["AccentColor"] is not Color accent) return;
        var tally = (Color)Application.Current.Resources["TallyColor"];
        var st = _studio.State;
        double ap = st switch
        {
            StudioState.Recording => 0.92,
            StudioState.Paused => 0.46,
            StudioState.Finishing => 0.04,
            StudioState.Arming => RecordIris.Aperture,
            _ => _hover ? 0.34 : 0.2,
        };
        double glow = st == StudioState.Recording ? 1 : st == StudioState.Paused ? 0.35 : 0;
        RecordIris.Core = st is StudioState.Recording or StudioState.Arming ? tally : accent;
        if (snap) { RecordIris.Aperture = ap; RecordIris.Glow = glow; return; }
        if (st != StudioState.Arming) Springs.To(RecordIris, Iris.ApertureProperty, ap, st == StudioState.Recording ? 160 : 300, st == StudioState.Recording ? 0.55 : 0.72);
        Springs.To(RecordIris, Iris.GlowProperty, glow, 120, 1);
        Springs.To(Logo, Iris.ApertureProperty, st == StudioState.Recording ? 0.85 : 0.42, 200, 0.7);
        Logo.Core = st == StudioState.Recording ? tally : accent;
    }

    /// <summary>Advances the timecode and the breathing lights (the demo calls it once per frame).</summary>
    public void Tick()
    {
        if (_studio.State is StudioState.Recording or StudioState.Paused)
        {
            var e = _studio.Elapsed;
            string tc = $"{(int)e.TotalHours:00}:{e.Minutes:00}:{e.Seconds:00}";
            Timecode.Text = tc;
            long bytes = _studio.Bytes;
            string size = bytes > 0 ? $" · {Format.Size(bytes)}" : "";
            RecChipText.Text = tc;
            FinderRecText.Text = (_studio.State == StudioState.Paused ? Loc.T("paused") : "REC") + " " + tc + size;
            // the dot and the open lens breathe while recording
            _breath += 0.1;
            double pulse = _studio.State == StudioState.Recording ? 0.55 + 0.45 * Math.Cos(_breath * 2.2) : 0.35;
            RecDot.Opacity = FinderDot.Opacity = pulse;
            if (_studio.State == StudioState.Recording) RecordIris.ScaleTurn += 0.6;
        }
    }

    // ── takes ───────────────────────────────────────────────────────────────

    public void BuildTakes()
    {
        TakesRow.Children.Clear();
        foreach (var t in _studio.Takes.Where(t => File.Exists(t.Path)).Take(24))
            TakesRow.Children.Add(TakeCard(t));
        TakesEmpty.Visibility = TakesRow.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TakesCount.Text = TakesRow.Children.Count > 0 ? TakesRow.Children.Count.ToString() : "";
        if (TakesRow.Children.Count > 0 && TakesRow.Children[0] is FrameworkElement first && (DateTime.Now - _studio.Takes[0].When).TotalSeconds < 5)
        {
            // a new take slides in from the left
            var tt = new TranslateTransform(-60, 0);
            first.RenderTransform = tt;
            first.Opacity = 0;
            Springs.To(tt, TranslateTransform.XProperty, 0, 260, 0.7);
            Springs.To(first, OpacityProperty, 1, 200, 1);
        }
    }

    FrameworkElement TakeCard(Take t)
    {
        var card = new Grid { Width = 150, Margin = new Thickness(0, 0, 12, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(84) });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var frame = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "Viewfinder");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        var inner = new Grid();
        if (t.Thumb != null && File.Exists(t.Thumb))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 360; bi.UriSource = new Uri(t.Thumb); bi.EndInit(); bi.Freeze();
                inner.Children.Add(new Image { Source = bi, Stretch = Stretch.UniformToFill });
            }
            catch { }
        }
        var badge = new Border { CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(0xB0, 8, 10, 13)), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = Format.Duration(t.Seconds), Foreground = Brushes.White, FontSize = 11, FontFamily = (FontFamily)FindResource("PlexMedium") } };
        inner.Children.Add(badge);
        var play = new Border { Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), Opacity = 0, Child = new Ico { Data = (Geometry)FindResource("IcoPlay"), FillShape = true, Width = 26, Height = 26, Foreground = Brushes.White } };
        inner.Children.Add(play);
        frame.Child = inner;
        card.Children.Add(frame);
        var text = new StackPanel { Margin = new Thickness(2, 6, 2, 0) };
        Grid.SetRow(text, 1);
        text.Children.Add(new TextBlock { Text = Format.When(t.When), FontSize = 12, FontFamily = (FontFamily)FindResource("PlexMedium"), TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = new TextBlock { Text = $"{Format.Size(t.Bytes)} · {t.Width}×{t.Height} · {t.Fps} fps", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
        text.Children.Add(sub);
        card.Children.Add(text);
        card.ToolTip = Path.GetFileName(t.Path);
        card.MouseEnter += (_, _) => Springs.To(play, OpacityProperty, 1, 300, 1);
        card.MouseLeave += (_, _) => Springs.To(play, OpacityProperty, 0, 300, 1);
        card.MouseLeftButtonUp += (_, _) => Core.Shell.Open(t.Path);
        var menu = new ContextMenu();
        void Item(string key, Action a) { var mi = new MenuItem { Header = Loc.T(key) }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("takes.play", () => Core.Shell.Open(t.Path));
        Item("takes.show", () => Core.Shell.Reveal(t.Path));
        Item("takes.copy", () => { var c = new System.Collections.Specialized.StringCollection { t.Path }; Clipboard.SetFileDropList(c); });
        menu.Items.Add(new Separator());
        Item("takes.delete", () => { if (Core.Shell.Recycle(t.Path)) { _studio.Forget(t); BuildTakes(); } });
        card.ContextMenu = menu;
        Press.SetEnabled(card, true);
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        return card;
    }

    // ── settings ────────────────────────────────────────────────────────────

    public void ShowSettings(bool show)
    {
        if (show)
        {
            _settings ??= new SettingsView(_studio, this);
            SettingsHost.Child = _settings;
            _settings.Reload();
            SettingsHost.Visibility = Visibility.Visible;
            Springs.To(SettingsMove, TranslateTransform.XProperty, 0, 300, 0.86, ActualWidth * 0.35);
            Springs.To(SettingsHost, OpacityProperty, 1, 300, 1, 0);
        }
        else
        {
            Springs.To(SettingsMove, TranslateTransform.XProperty, ActualWidth * 0.3, 300, 1);
            Springs.To(SettingsHost, OpacityProperty, 0, 380, 1);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            t.Tick += (_, _) => { t.Stop(); if (SettingsHost.Opacity < 0.2) SettingsHost.Visibility = Visibility.Collapsed; };
            t.Start();
            SourceSeg.Value = S.Source;
            QualitySeg.Value = NearestQuality(S.Quality);
            SysToggle.IsChecked = S.SystemAudio;
            MicToggle.IsChecked = S.Microphone;
            ForgetDeviceNames();
            Refresh();
            RestartPreview();
            Meters();
        }
    }

    // ── demo hooks (README pictures) ────────────────────────────────────────
    public void SettingsPage(string page) => _settings?.Open(page);
    public void DemoHover(bool on) { _hover = on; ApplyIris(); }
    public void DemoCount(int n) => OnCount(n);

    public void ShowAndFocus()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Win32.ForceForeground(new WindowInteropHelper(this).Handle);
    }
}

/// <summary>Human-friendly numbers.</summary>
public static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    public static string Duration(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string When(DateTime when)
    {
        var d = DateTime.Now - when;
        if (d.TotalMinutes < 1) return Loc.T("ago.now");
        if (d.TotalMinutes < 60) return Loc.F("ago.min", (int)d.TotalMinutes);
        if (d.TotalHours < 12) return Loc.F("ago.hour", (int)d.TotalHours);
        if (when.Date == DateTime.Today.AddDays(-1)) return Loc.T("ago.yesterday") + " " + when.ToString("HH:mm");
        return when.ToString(Loc.Turkish ? "d MMM HH:mm" : "MMM d, HH:mm", Loc.Turkish ? new System.Globalization.CultureInfo("tr-TR") : System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Small icons of other apps' windows, for the window list.</summary>
public static class AppIcons
{
    static readonly Dictionary<uint, ImageSource?> Cache = [];
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

    public static ImageSource? For(IntPtr hwnd, uint pid)
    {
        if (Cache.TryGetValue(pid, out var img)) return img;
        IntPtr icon = IntPtr.Zero;
        SendMessageTimeout(hwnd, 0x007F, (IntPtr)2, IntPtr.Zero, 0x2, 50, out icon); // WM_GETICON ICON_SMALL2
        if (icon == IntPtr.Zero) SendMessageTimeout(hwnd, 0x007F, IntPtr.Zero, IntPtr.Zero, 0x2, 50, out icon);
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(hwnd, -34); // GCLP_HICONSM
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(hwnd, -14);
        try
        {
            if (icon != IntPtr.Zero)
            {
                img = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                img.Freeze();
            }
        }
        catch { img = null; }
        return Cache[pid] = img;
    }
}
