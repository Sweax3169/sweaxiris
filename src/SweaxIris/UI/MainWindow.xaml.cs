using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// The studio. One page answers the three questions in order: what to record (big live pictures of every
/// monitor and window to click), with which sound (two faders that are also meters), at what quality; the
/// record strip at the bottom shows the choice and the iris. Takes and Settings are pages of their own.
/// </summary>
public partial class MainWindow : Window
{
    readonly Studio _studio;
    Settings S => _studio.S;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _windowsRefresh = new() { Interval = TimeSpan.FromSeconds(4) };
    Thumbnails? _thumbs;
    CaptureStream? _sysMonitor, _micMonitor;
    SettingsView? _settings;
    readonly List<SourceTile> _tiles = [];
    readonly Dictionary<string, ImageSource> _pictures = [];
    string _windowSet = "";
    string _page = "record";
    bool _hover, _ready;
    double _breath;

    /// <summary>Set by the app: what the record button and the region button do.</summary>
    public Action? RecordRequested, RegionRequested, PauseRequested, MicRequested;

    /// <summary>For demo pictures: a picture per source key instead of live capture.</summary>
    public static Func<string, BitmapSource?>? DemoPicture { get; set; }

    public MainWindow(Studio studio)
    {
        _studio = studio;
        InitializeComponent();

        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();
        OpenFolder.Click += (_, _) => { Directory.CreateDirectory(S.Folder); Core.Shell.Open(S.Folder); };
        PauseButton.Click += (_, _) => PauseRequested?.Invoke();
        MicButton.Click += (_, _) => MicRequested?.Invoke();
        MoreVideo.Click += (_, _) => { Page("settings"); _settings?.Open("video"); };
        RefreshWindows.Click += (_, _) => BuildPicks();
        Search.TextChanged += (_, _) => BuildPicks();

        BuildTabs();
        Mode(ModeScreen, "IcoScreen", "src.screen", "src.screen.hint");
        Mode(ModeWindow, "IcoWindow", "src.window", "src.window.hint");
        Mode(ModeRegion, "IcoRegion", "src.region", "src.region.hint");

        // the record control: hover opens the iris, a press stops it down, release records
        RecordHost.RenderTransformOrigin = new Point(0.5, 0.5);
        RecordHost.MouseEnter += (_, _) => { _hover = true; ApplyIris(); };
        RecordHost.MouseLeave += (_, _) => { _hover = false; ApplyIris(); Springs.To(RecordScale, ScaleTransform.ScaleXProperty, 1, 520, 0.6); Springs.To(RecordScale, ScaleTransform.ScaleYProperty, 1, 520, 0.6); };
        RecordHost.MouseLeftButtonDown += (_, e) =>
        {
            Springs.To(RecordScale, ScaleTransform.ScaleXProperty, 0.97, 800, 0.9);
            Springs.To(RecordScale, ScaleTransform.ScaleYProperty, 0.97, 800, 0.9);
            if (_studio.State == StudioState.Idle) Springs.To(RecordIris, Iris.ApertureProperty, 0.08, 700, 0.9);
            e.Handled = true;
        };
        RecordHost.MouseLeftButtonUp += (_, _) =>
        {
            Springs.To(RecordScale, ScaleTransform.ScaleXProperty, 1, 520, 0.55);
            Springs.To(RecordScale, ScaleTransform.ScaleYProperty, 1, 520, 0.55);
            RecordRequested?.Invoke();
        };

        // sound
        SysToggle.IsChecked = S.SystemAudio;
        MicToggle.IsChecked = S.Microphone;
        SysToggle.Click += (_, _) => { S.SystemAudio = SysToggle.IsChecked == true; S.Save(); Meters(); Refresh(); };
        MicToggle.Click += (_, _) => { S.Microphone = MicToggle.IsChecked == true; S.Save(); Meters(); Refresh(); };
        SysFader.Value = S.SystemVolume;
        MicFader.Value = S.MicVolume;
        SysFader.Changed += v => { S.SystemVolume = v; _studio.SetVolumes(); Pct(); };
        MicFader.Changed += v => { S.MicVolume = v; _studio.SetVolumes(); Pct(); };
        SysFader.Released += S.Save;
        MicFader.Released += S.Save;
        SysFader.Level = () => _studio.SystemPeak is >= 0 and var p ? p : (_sysMonitor?.Peak ?? 0) * (float)S.SystemVolume;
        MicFader.Level = () => _studio.MicPeak is >= 0 and var p ? p : (_micMonitor?.Peak ?? 0) * (float)S.MicVolume * (_studio.MicMuted ? 0 : 1);
        FillDevices();
        SysDevice.SelectionChanged += (_, _) => { if (SysDevice.SelectedItem is ComboBoxItem i && (string)i.Tag != S.SystemDevice) { S.SystemDevice = (string)i.Tag; S.Save(); RestartMeters(); } };
        MicDevice.SelectionChanged += (_, _) => { if (MicDevice.SelectedItem is ComboBoxItem i && (string)i.Tag != S.MicDevice) { S.MicDevice = (string)i.Tag; S.Save(); RestartMeters(); } };

        // picture
        Fill(ResCombo, new[] { 0, 2160, 1440, 1080, 720, 480 }.Select(h => ((object)h, h == 0 ? Loc.T("video.native") : $"{h}p")), S.Resolution, v => { S.Resolution = (int)v; Refresh(); });
        Fill(FpsCombo, new[] { 24, 30, 48, 50, 60, 90, 120, 144, 165, 240 }.Select(f => ((object)f, Loc.F("video.fps", f))), S.Fps, v => { S.Fps = (int)v; Refresh(); });
        FillQuality();

        _studio.StateChanged += OnState;
        _studio.CountdownTick += OnCount;
        _studio.RecordingPreview += bmp => { if (_studio.State is StudioState.Recording or StudioState.Paused) { Strip(bmp); LivePicture.Source = bmp; } };
        _studio.EncodersReady += Refresh;
        _studio.Saved += _ => BuildTakes();

        _clock.Tick += (_, _) => { if (!Studio.Demo) Tick(); };
        // windows come and go: rebuild the window tiles only when the set of windows changed
        _windowsRefresh.Tick += (_, _) => { if (S.Source == "window" && _page == "record" && IsVisible && !Studio.Demo && WindowSet() != _windowSet) BuildPicks(); };
        IsVisibleChanged += (_, _) => { Live(); Meters(); };
        StateChanged += (_, _) => Live();
        Theme.Changed += ApplyIris;
        SourceInitialized += (_, _) => ApplyCaptureExclusion();
        SizeChanged += (_, e) => { if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 40) { BuildPicks(); if (_page == "library") BuildTakes(); } };
        Loaded += (_, _) =>
        {
            _ready = true;
            PlaceTabMark(false);
            ShowMode();
            Refresh();
            BuildTakes();
            ApplyIris(true);
            _clock.Start();
            _windowsRefresh.Start();
        };
        Closing += OnClosing;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _page == "settings") { Page("record"); e.Handled = true; } };
    }

    public void ApplyCaptureExclusion()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) Win32.SetWindowDisplayAffinity(h, S.HideFromCapture ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE);
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        // the close button tucks SweaxIris into the tray; the shortcuts keep working
        if (App.Quitting) { _thumbs?.Dispose(); _thumbs = null; return; }
        e.Cancel = true;
        Hide();
    }

    // ── pages ───────────────────────────────────────────────────────────────

    readonly Dictionary<string, Button> _tabButtons = [];

    void BuildTabs()
    {
        foreach (var (key, text) in new[] { ("record", "tab.record"), ("library", "tab.library"), ("settings", "tab.settings") })
        {
            var b = new Button { Style = (Style)FindResource("TabBtn"), Content = Loc.T(text), Height = 56 };
            Mark.SetOn(b, key == _page);
            b.Click += (_, _) => Page(key);
            Tabs.Children.Add(b);
            _tabButtons[key] = b;
        }
    }

    void PlaceTabMark(bool animate)
    {
        if (!_tabButtons.TryGetValue(_page, out var b)) return;
        Tabs.UpdateLayout();
        TabsHost.Width = Tabs.ActualWidth;
        double x = b.TranslatePoint(new Point(0, 0), Tabs).X + 16, w = Math.Max(10, b.ActualWidth - 32);
        if (animate)
        {
            Springs.To(TabMove, TranslateTransform.XProperty, x, 420, 0.75);
            Springs.To(TabMark, WidthProperty, w, 420, 0.75);
        }
        else { Springs.Set(TabMove, TranslateTransform.XProperty, x); Springs.Set(TabMark, WidthProperty, w); }
    }

    public void Page(string page)
    {
        if (page == "settings" && _settings == null)
        {
            _settings = new SettingsView(_studio, this);
            SettingsHost.Child = _settings;
        }
        if (_page == "settings" && page != "settings") { FillDevices(); FillQuality(); SysToggle.IsChecked = S.SystemAudio; MicToggle.IsChecked = S.Microphone; Refresh(); }
        _page = page;
        foreach (var (k, b) in _tabButtons) Mark.SetOn(b, k == page);
        PlaceTabMark(true);
        FrameworkElement show = page switch { "library" => LibraryPage, "settings" => SettingsHost, _ => RecordPage };
        foreach (var p in new FrameworkElement[] { RecordPage, LibraryPage, SettingsHost }) p.Visibility = p == show ? Visibility.Visible : Visibility.Collapsed;
        if (page == "settings") _settings!.Reload();
        if (page == "library") BuildTakes();
        var tt = new TranslateTransform(0, 14);
        show.RenderTransform = tt;
        show.Opacity = 0;
        Springs.To(tt, TranslateTransform.YProperty, 0, 340, 0.8);
        Springs.To(show, OpacityProperty, 1, 260, 1);
        Live();
    }

    /// <summary>Kept for the app and the demo: Settings is a page now.</summary>
    public void ShowSettings(bool show) => Page(show ? "settings" : "record");

    // ── what to record ──────────────────────────────────────────────────────

    void Mode(Button b, string icon, string title, string hint)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var ico = new Ico { Data = (Geometry)FindResource(icon), Width = 26, Height = 26, Margin = new Thickness(0, 0, 14, 0) };
        g.Children.Add(ico);
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 16, FontWeight = FontWeights.SemiBold });
        var sub = new TextBlock { Text = Loc.T(hint), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        t.Children.Add(sub);
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        b.Content = g;
        b.Click += (_, _) =>
        {
            string mode = (string)b.Tag;
            if (_studio.State != StudioState.Idle) return;
            if (mode == "region" && S.Source == "region" && _studio.LastRegion == null) { RegionRequested?.Invoke(); return; }
            S.Source = mode;
            S.Save();
            ShowMode();
            Refresh();
        };
    }

    void ShowMode()
    {
        foreach (var b in new[] { ModeScreen, ModeWindow, ModeRegion })
        {
            bool on = (string)b.Tag == S.Source;
            Mark.SetOn(b, on);
            if (((Grid)b.Content).Children[0] is Ico i) i.SetResourceReference(Ico.ForegroundProperty, on ? "Accent" : "TextDim");
        }
        PickTitle.Text = Loc.T(S.Source switch { "window" => "pick.window", "region" => "pick.region", _ => "pick.screen" });
        SearchBox.Visibility = RefreshWindows.Visibility = S.Source == "window" ? Visibility.Visible : Visibility.Collapsed;
        BuildPicks();
        Picks.Opacity = 0;
        var tt = new TranslateTransform(0, 10);
        Picks.RenderTransform = tt;
        Springs.To(Picks, OpacityProperty, 1, 260, 1);
        Springs.To(tt, TranslateTransform.YProperty, 0, 320, 0.8);
    }

    /// <summary>Tiles fill the page width exactly; each carries an 18 px gutter on its right that the panel lets overflow.</summary>
    double TileWidth(int perRow)
    {
        double w = RecordPage.ActualWidth > 100 ? RecordPage.ActualWidth : ActualWidth - 64;
        return Math.Floor((w - (perRow - 1) * 18) / perRow - 0.5);
    }

    /// <summary>Builds the tiles of the current mode and starts their live pictures.</summary>
    void BuildPicks()
    {
        if (!_ready) return;
        Picks.Children.Clear();
        _tiles.Clear();
        var keep = new List<string>();
        SourceTile Add(SourceTile t, string? key, Func<Gpu, IVideoSource>? source, bool selected)
        {
            t.Margin = new Thickness(0, 0, 18, 18);
            t.Selected = selected;
            t.PictureKey = key;
            Picks.Children.Add(t);
            _tiles.Add(t);
            if (key != null)
            {
                keep.Add(key);
                // a rebuilt tile starts from its last picture, so nothing blinks
                if (_pictures.TryGetValue(key, out var known)) t.SetPicture(known, fade: false);
                if (DemoPicture?.Invoke(key) is { } demo) t.SetPicture(demo);
                else if (source != null && _thumbs != null) _thumbs.Watch(key, source, selected ? 6 : 1.5, selected ? 640 : 400, bmp => { _pictures[key] = bmp; t.SetPicture(bmp); if (t.Selected && _studio.Recorder == null) Strip(bmp); });
                if (selected && _thumbs != null) _thumbs.Rate(key, 6);
            }
            return t;
        }

        switch (S.Source)
        {
            case "window":
            {
                double w = TileWidth(4);
                string q = Search.Text.Trim();
                var wins = Studio.DemoWindows is { } demo
                    ? demo.Select((d, i) => ((IntPtr)(i + 1), d.Title, d.App)).ToList()
                    : Win32.RealWindows().Select(h => { Win32.GetWindowThreadProcessId(h, out uint pid); return (h, Win32.GetWindowTitle(h), Path.GetFileNameWithoutExtension(Win32.GetProcessPath(pid) ?? "")); }).ToList();
                if (_studio.ChosenWindow == IntPtr.Zero && Studio.Demo && wins.Count > 0) _studio.ChosenWindow = wins[0].Item1;
                _windowSet = string.Join("|", wins.Select(x => x.Item1));
                foreach (var (h, title, app) in wins.Where(x => q.Length == 0 || x.Item2.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Item3.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(24))
                {
                    ImageSource? icon = null;
                    if (!Studio.Demo) { Win32.GetWindowThreadProcessId(h, out uint pid); icon = AppIcons.For(h, pid); }
                    var r = Studio.Demo ? default : Win32.FrameOf(h);
                    var tile = new SourceTile(w, title, Studio.Demo ? app : $"{app} · {r.Width} × {r.Height}", icon) { Key = h };
                    var hwnd = h;
                    tile.Picked += t => { _studio.ChosenWindow = hwnd; Select(t); };
                    Add(tile, "win:" + h, Thumbnails.Window(h, 1.5), h == _studio.ChosenWindow);
                }
                break;
            }
            case "region":
            {
                double w = TileWidth(3);
                var draw = new SourceTile(w, Loc.T("tile.draw"), Loc.T("tile.draw.d"), placeholder: DrawPlaceholder());
                draw.Picked += _ => RegionRequested?.Invoke();
                Add(draw, null, null, false);
                if (_studio.LastRegion is { } r)
                {
                    var mon = Studio.Monitors().FirstOrDefault(m => !Studio.Demo && m.Handle == Win32.MonitorFromRect(ref r, Win32.MONITOR_DEFAULTTONEAREST));
                    var last = new SourceTile(w, Loc.T("tile.last") + $" · {r.Width} × {r.Height}", $"{r.Left}, {r.Top}{(mon != null ? " · " + mon.Title : "")}");
                    last.Picked += t => Select(t);
                    Add(last, $"reg:{r.Left},{r.Top},{r.Right},{r.Bottom}", Thumbnails.Region(r, 4), true);
                }
                break;
            }
            default:
            {
                var mons = Studio.Monitors();
                double w = TileWidth(Math.Clamp(mons.Count + 1, 2, 4));
                var mouse = new SourceTile(w, Loc.T("tile.mouse"), Loc.T("tile.mouse.d"), placeholder: MousePlaceholder()) { Key = "" };
                mouse.Picked += t => { S.Monitor = ""; S.Save(); Select(t); };
                Add(mouse, null, null, string.IsNullOrEmpty(S.Monitor));
                foreach (var m in mons)
                {
                    string sub = $"{m.Bounds.Width} × {m.Bounds.Height}{(m.Hz > 0 ? $" · {Math.Round(m.Hz)} Hz" : "")}{(m.Primary ? " · " + Loc.T("tile.primary") : "")}";
                    var tile = new SourceTile(w, m.Title, sub) { Key = m.Device };
                    var dev = m.Device;
                    tile.Picked += t => { S.Monitor = dev; S.Save(); Select(t); };
                    Add(tile, "mon:" + m.Device, Thumbnails.Monitor(m.Handle, 1.5), S.Monitor.Equals(m.Device, StringComparison.OrdinalIgnoreCase));
                }
                // with more than one monitor, the arrangement tells them apart
                if (mons.Count > 1)
                {
                    double full = TileWidth(1);
                    var head = new TextBlock { Text = Loc.T("map.title"), FontSize = 12.5, Width = full, Margin = new Thickness(0, 0, 18, 8) };
                    head.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
                    Picks.Children.Add(head);
                    Picks.Children.Add(new MonitorMap(mons, S.Monitor, dev => { S.Monitor = dev; S.Save(); BuildPicks(); Refresh(); }, full, 80) { Margin = new Thickness(0, 0, 18, 8) });
                }
                break;
            }
        }
        _thumbs?.Keep(keep);
        if (_pictures.Count > 48) foreach (var k in _pictures.Keys.Except(keep).ToList()) _pictures.Remove(k);
        UpdateStripPicture();
    }

    static string WindowSet() => string.Join("|", Win32.RealWindows());

    /// <summary>Chooses a tile in place (its focus pull animates); the chosen one's picture refreshes faster.</summary>
    void Select(SourceTile t)
    {
        foreach (var x in _tiles)
        {
            x.Selected = ReferenceEquals(x, t);
            if (x.PictureKey != null) _thumbs?.Rate(x.PictureKey, x.Selected ? 6 : 1.5);
        }
        UpdateStripPicture();
        Refresh();
    }

    void UpdateStripPicture()
    {
        if (_studio.Recorder != null) return;
        var chosen = _tiles.FirstOrDefault(t => t.Selected);
        PreviewIco.Data = (Geometry)FindResource(S.Source switch { "window" => "IcoWindow", "region" => "IcoRegion", _ => "IcoScreen" });
        ImageSource? pic = null;
        if (chosen != null && DemoPicture != null) pic = DemoPicture(chosen.PictureKey ?? "mon:mouse");
        Strip(pic ?? chosen?.Picture);
    }

    /// <summary>The small picture at the left of the record strip; the source icon stands in until one arrives.</summary>
    void Strip(ImageSource? pic)
    {
        Preview.Source = pic;
        PreviewIco.Visibility = pic == null ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement DrawPlaceholder()
    {
        var g = new Grid();
        var dash = new System.Windows.Shapes.Rectangle { StrokeDashArray = [5, 4], StrokeThickness = 2, RadiusX = 8, RadiusY = 8, Margin = new Thickness(26) };
        dash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
        g.Children.Add(dash);
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ico = new Ico { Width = 34, Height = 34, Data = (Geometry)Application.Current.FindResource("IcoRegion") };
        ico.SetResourceReference(Ico.ForegroundProperty, "Accent");
        sp.Children.Add(ico);
        var keys = KeyCaps.Build(Hotkey.Parse(S.KeyRegion), 11);
        keys.HorizontalAlignment = HorizontalAlignment.Center;
        keys.Margin = new Thickness(0, 10, 0, 0);
        sp.Children.Add(keys);
        g.Children.Add(sp);
        return g;
    }

    FrameworkElement MousePlaceholder()
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Orientation = Orientation.Horizontal };
        foreach (var w in new[] { 40.0, 52, 40 })
        {
            var r = new Border { Width = w, Height = w * 0.62, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5), Margin = new Thickness(4, 0, 4, 0) };
            r.SetResourceReference(Border.BorderBrushProperty, "TextFaint");
            sp.Children.Add(r);
        }
        var g = new Grid();
        g.Children.Add(sp);
        var cur = new Ico { Width = 26, Height = 26, Data = (Geometry)Application.Current.FindResource("IcoCursor"), FillShape = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 22, 0, 0) };
        cur.SetResourceReference(Ico.ForegroundProperty, "Accent");
        g.Children.Add(cur);
        return g;
    }

    public void RegionChanged()
    {
        ShowMode();
        Refresh();
    }

    /// <summary>Live pictures run only while the record page is on screen and nothing records.</summary>
    void Live()
    {
        bool want = IsVisible && WindowState != WindowState.Minimized && _page == "record" && _studio.Recorder == null && !Studio.Demo;
        if (want && _thumbs == null)
        {
            try { _thumbs = new Thumbnails(Dispatcher); BuildPicks(); }
            catch (Exception ex) { Paths.Log("thumbnails: " + ex.Message); }
        }
        else if (!want && _thumbs != null)
        {
            var t = _thumbs;
            _thumbs = null;
            Task.Run(t.Dispose);
        }
    }

    public void RestartPreview() => Live();

    /// <summary>Demo pictures: shows a frame in the chosen tile and the strip.</summary>
    public void ShowFrame(BitmapSource bmp)
    {
        Strip(bmp);
        LivePicture.Source = bmp;
        _tiles.FirstOrDefault(t => t.Selected)?.SetPicture(bmp);
    }

    // ── sound ───────────────────────────────────────────────────────────────

    void FillDevices()
    {
        FillDevice(SysDevice, EDataFlow.Render, S.SystemDevice, MainWindow.DemoSystemDevice);
        FillDevice(MicDevice, EDataFlow.Capture, S.MicDevice, MainWindow.DemoMicDevice);
        Pct();
    }

    void FillDevice(ComboBox cb, EDataFlow flow, string current, string? demo)
    {
        var items = new List<ComboBoxItem>();
        if (Studio.Demo) items.Add(new ComboBoxItem { Content = demo ?? Loc.T("sound.default"), Tag = "" });
        else
        {
            var list = AudioDevices.List(flow);
            var def = list.FirstOrDefault(d => d.IsDefault);
            items.Add(new ComboBoxItem { Content = def != null ? $"{Loc.T("sound.default")} · {def.Name}" : Loc.T("sound.default"), Tag = "" });
            items.AddRange(list.Select(d => new ComboBoxItem { Content = d.Name, Tag = d.Id }));
        }
        cb.ItemsSource = items;
        cb.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == current) ?? items[0];
    }

    void Pct()
    {
        SysPct.Text = Loc.F("vol", Math.Round(S.SystemVolume * 100));
        MicPct.Text = Loc.F("vol", Math.Round(S.MicVolume * 100));
        SysFader.Off = !S.SystemAudio;
        MicFader.Off = !S.Microphone || _studio.MicMuted;
    }

    void RestartMeters()
    {
        foreach (var m in new[] { _sysMonitor, _micMonitor }) if (m != null) Task.Run(m.Dispose);
        _sysMonitor = _micMonitor = null;
        Meters();
    }

    void Meters()
    {
        bool idle = IsVisible && _studio.Recorder == null && !Studio.Demo;
        bool sys = idle && S.SystemAudio, mic = idle && S.Microphone;
        if (sys && _sysMonitor == null) { _sysMonitor = new CaptureStream(CaptureStream.Kind.System, S.SystemDevice); _sysMonitor.Start(); }
        if (!sys && _sysMonitor != null) { var m = _sysMonitor; _sysMonitor = null; Task.Run(m.Dispose); }
        if (mic && _micMonitor == null) { _micMonitor = new CaptureStream(CaptureStream.Kind.Microphone, S.MicDevice); _micMonitor.Start(); }
        if (!mic && _micMonitor != null) { var m = _micMonitor; _micMonitor = null; Task.Run(m.Dispose); }
        Pct();
    }

    // ── picture ─────────────────────────────────────────────────────────────

    void Fill(ComboBox cb, IEnumerable<(object Value, string Label)> items, object current, Action<object> set)
    {
        var list = items.Select(i => new ComboBoxItem { Content = i.Label, Tag = i.Value }).ToList();
        cb.ItemsSource = list;
        cb.SelectedItem = list.FirstOrDefault(i => Equals(i.Tag, current)) ?? list[0];
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem i) { set(i.Tag); S.Save(); } };
    }

    static readonly double[] Qualities = [0.30, 0.55, 0.72, 0.92];
    static double NearestQuality(double q) => Qualities.OrderBy(x => Math.Abs(x - q)).First();
    static string QualityKey(double q) => NearestQuality(q) switch { 0.30 => "q.small", 0.55 => "q.balanced", 0.72 => "q.high", _ => "q.max" };

    bool _fillingQuality;

    void FillQuality()
    {
        _fillingQuality = true;
        var list = Qualities.Select(q => new ComboBoxItem { Content = Loc.T(QualityKey(q)), Tag = q }).ToList();
        list.Add(new ComboBoxItem { Content = Loc.F("q.bitrate", S.BitrateKbps / 1000), Tag = -1.0 });
        QualityCombo.ItemsSource = list;
        QualityCombo.SelectedItem = S.ConstantQuality ? list.First(i => (double)i.Tag == NearestQuality(S.Quality)) : list[^1];
        if (QualityCombo.Tag == null)
        {
            QualityCombo.Tag = true;
            QualityCombo.SelectionChanged += (_, _) =>
            {
                if (_fillingQuality || QualityCombo.SelectedItem is not ComboBoxItem i) return;
                double q = (double)i.Tag;
                if (q < 0) S.ConstantQuality = false;
                else { S.ConstantQuality = true; S.Quality = q; }
                S.Save();
                Refresh();
            };
        }
        _fillingQuality = false;
    }

    // ── state ───────────────────────────────────────────────────────────────

    /// <summary>Re-reads the settings into the record strip.</summary>
    public void Refresh()
    {
        if (!_ready) return;
        var t = _studio.CurrentTarget();
        int sw = 0, sh = 0;
        string what = "";
        if (Studio.Demo && DemoSource is { } ds)
        {
            (what, sw, sh) = ds;
            if (S.Source == "window" && Studio.DemoWindows is { Count: > 0 } dw) (what, sw, sh) = (dw[Math.Clamp((int)_studio.ChosenWindow - 1, 0, dw.Count - 1)].Title, 1920, 1080);
            if (S.Source == "region" && _studio.LastRegion is { } lr) (what, sw, sh) = (Loc.T("src.region"), lr.Width, lr.Height);
        }
        else if (t != null)
        {
            if (t.Kind == SourceKind.Region) { sw = t.Region.Width; sh = t.Region.Height; what = Loc.T("src.region"); }
            else if (t.Kind == SourceKind.Window) { var f = Win32.FrameOf(t.Window); sw = f.Width; sh = f.Height; what = Win32.GetWindowTitle(t.Window); }
            else if (_studio.ChosenMonitor() is { } m) { sw = m.Bounds.Width; sh = m.Bounds.Height; what = string.IsNullOrEmpty(S.Monitor) ? $"{m.Title} ({Loc.T("tile.mouse").ToLowerInvariant()})" : m.Title; }
        }
        var enc = _studio.PickEncoder(t);
        string encText = _studio.Encoders == null && !Studio.Demo ? Loc.T("set.encoder.checking") : $"{enc} · {S.Container.ToUpperInvariant()}";
        EncoderLine.Text = encText;
        if (_studio.State == StudioState.Idle)
        {
            if (sw > 0)
            {
                double sc = S.Resolution > 0 && S.Resolution < sh ? (double)S.Resolution / sh : 1;
                int ow = (int)Math.Round(sw * sc / 2) * 2, oh = (int)Math.Round(sh * sc / 2) * 2;
                StripTitle.Text = what;
                string q = S.ConstantQuality ? Loc.T(QualityKey(S.Quality)) : Loc.F("q.bitrate", S.BitrateKbps / 1000);
                string sound = string.Join(" + ", new[] { S.SystemAudio ? Loc.T("sound.system") : null, S.Microphone ? Loc.T("sound.mic") : null }.Where(x => x != null));
                StripSub.Text = $"{ow} × {oh} · {Loc.F("video.fps", S.Fps)} · {q} · {encText}{(sound.Length > 0 ? " · " + sound : "")}";
            }
            else
            {
                StripTitle.Text = Loc.T("strip.nothing");
                StripSub.Text = Loc.T(S.Source == "window" ? "src.nowindow" : "src.noregion");
            }
        }
        RecordKeys.Content = KeyCaps.Build(Hotkey.Parse(S.KeyRecord), 11);
        UpdateDeck();
    }

    static readonly Dictionary<string, string> _deviceNames = [];
    public static void ForgetDeviceNames() => _deviceNames.Clear();
    public static string? DemoSystemDevice, DemoMicDevice;
    public static (string What, int W, int H)? DemoSource;

    void OnState()
    {
        UpdateDeck();
        Meters();
        Live();
        if (_studio.State == StudioState.Idle) { BuildPicks(); Refresh(); }
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
        PauseButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        MicButton.Visibility = live && S.Microphone ? Visibility.Visible : Visibility.Collapsed;
        Timecode.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        StripDot.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        PauseIco.Data = (Geometry)FindResource(st == StudioState.Paused ? "IcoPlay" : "IcoPause");
        PauseIco.FillShape = st == StudioState.Paused;
        PauseText.Text = Loc.T(st == StudioState.Paused ? "resume" : "pause");
        MicIco.Data = (Geometry)FindResource(_studio.MicMuted ? "IcoMicOff" : "IcoMic");
        MicRowIco.Data = MicIco.Data;
        MicButton.SetResourceReference(ForegroundProperty, _studio.MicMuted ? "Tally" : "Text");
        RecordWord.Text = st switch
        {
            StudioState.Arming => Loc.T("arming"),
            StudioState.Recording or StudioState.Paused => Loc.T("stop"),
            StudioState.Finishing => Loc.T("finishing"),
            _ => Loc.T("record"),
        };
        RecordKeys.Visibility = st is StudioState.Idle or StudioState.Recording or StudioState.Paused ? Visibility.Visible : Visibility.Collapsed;
        RecordHost.SetResourceReference(Border.BorderBrushProperty, live ? "Tally" : st == StudioState.Idle ? "AccentLine" : "Line");
        RecordHost.SetResourceReference(Border.BackgroundProperty, live ? "TallySoft" : "Raised");
        RecChip.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in new[] { ModeScreen, ModeWindow, ModeRegion }) { b.IsEnabled = st == StudioState.Idle; Springs.To(b, OpacityProperty, st == StudioState.Idle ? 1 : 0.42, 300, 1); }
        // while recording the picker gives way to the program picture
        if (live != (LiveView.Visibility == Visibility.Visible))
        {
            LiveView.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            if (live)
            {
                var sc = new ScaleTransform(0.97, 0.97);
                LiveView.RenderTransformOrigin = new Point(0.5, 0.5);
                LiveView.RenderTransform = sc;
                LiveView.Opacity = 0;
                Springs.To(sc, ScaleTransform.ScaleXProperty, 1, 380, 0.7);
                Springs.To(sc, ScaleTransform.ScaleYProperty, 1, 380, 0.7);
                Springs.To(LiveView, OpacityProperty, 1, 260, 1);
            }
        }
        if (live)
        {
            LiveView.SetResourceReference(Border.BorderBrushProperty, st == StudioState.Paused ? "Accent" : "Tally");
            LiveChip.SetResourceReference(Border.BackgroundProperty, st == StudioState.Paused ? "Accent" : "Tally");
            if (st == StudioState.Paused) { LiveText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText"); LiveDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentText"); }
            else { LiveText.Foreground = Brushes.White; LiveDot.Fill = Brushes.White; }
            LiveNote.Text = StudioState.Paused == st ? Loc.T("live.paused") : "";
        }
        if (live)
        {
            StripTitle.Text = Loc.T(st == StudioState.Paused ? "paused" : "recording");
            StripTitle.SetResourceReference(TextBlock.ForegroundProperty, st == StudioState.Paused ? "Accent" : "Tally");
            StripDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, st == StudioState.Paused ? "Accent" : "Tally");
        }
        else StripTitle.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        ApplyIris();
        Pct();
    }

    /// <summary>Springs the iris to the look of the current state.</summary>
    void ApplyIris() => ApplyIris(false);

    void ApplyIris(bool snap)
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
            _ => _hover ? 0.38 : 0.22,
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
            RecChipText.Text = tc;
            LiveText.Text = $"{(_studio.State == StudioState.Paused ? Loc.T("paused") : "REC")}  {tc}".ToUpperInvariant();
            long bytes = _studio.Bytes;
            var rec = _studio.Recorder;
            string target = _studio.Target?.Kind switch { SourceKind.Window => Loc.T("src.window"), SourceKind.Region => Loc.T("src.region"), _ => Loc.T("src.screen") };
            StripSub.Text = $"{target} · {Loc.F("video.fps", S.Fps)}{(bytes > 0 ? " · " + Format.Size(bytes) : "")}{(rec?.Dropped > 0 ? " · " + Loc.F("stats.dropped", rec.Dropped) : "")}";
            _breath += 0.1;
            double pulse = _studio.State == StudioState.Recording ? 0.55 + 0.45 * Math.Cos(_breath * 2.2) : 0.35;
            RecDot.Opacity = StripDot.Opacity = pulse;
            LiveDot.Opacity = 0.35 + 0.65 * pulse;
        }
    }

    // ── takes ───────────────────────────────────────────────────────────────

    public void BuildTakes()
    {
        TakesRow.Children.Clear();
        var takes = _studio.Takes.Where(t => Studio.Demo || File.Exists(t.Path)).Take(60).ToList();
        foreach (var t in takes) TakesRow.Children.Add(TakeCard(t));
        TakesEmpty.Visibility = takes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        long bytes = takes.Sum(t => t.Bytes);
        double secs = takes.Sum(t => t.Seconds);
        TakesCount.Text = takes.Count == 0 ? "" : $"{takes.Count} · {Format.Duration(secs)} · {Format.Size(bytes)} · {S.Folder}";
        _tabButtons.TryGetValue("library", out var tab);
        if (tab != null) tab.Content = takes.Count > 0 ? $"{Loc.T("tab.library")}  {takes.Count}" : Loc.T("tab.library");
        if (TakesRow.Children.Count > 0 && TakesRow.Children[0] is FrameworkElement first && takes.Count > 0 && (DateTime.Now - takes[0].When).TotalSeconds < 5)
        {
            var tt = new TranslateTransform(-40, 0);
            first.RenderTransform = tt;
            first.Opacity = 0;
            Springs.To(tt, TranslateTransform.XProperty, 0, 260, 0.7);
            Springs.To(first, OpacityProperty, 1, 200, 1);
        }
        PlaceTabMark(false);
    }

    FrameworkElement TakeCard(Take t)
    {
        // cards fill the page: as many ~300 px columns as fit
        double page = LibraryPage.ActualWidth > 100 ? LibraryPage.ActualWidth : RecordPage.ActualWidth > 100 ? RecordPage.ActualWidth : ActualWidth - 64;
        int cols = Math.Max(2, (int)((page + 18) / 318));
        double W = Math.Floor((page - (cols - 1) * 18) / cols - 0.5);
        var card = new Grid { Width = W, Margin = new Thickness(0, 0, 18, 22), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(W * 9 / 16) });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var frame = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "Viewfinder");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        var inner = new Grid();
        if (t.Thumb != null && File.Exists(t.Thumb))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 500; bi.UriSource = new Uri(Path.GetFullPath(t.Thumb)); bi.EndInit(); bi.Freeze();
                inner.Children.Add(new Image { Source = bi, Stretch = Stretch.UniformToFill });
            }
            catch { }
        }
        inner.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(0xB8, 8, 10, 13)), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = Format.Duration(t.Seconds), Foreground = Brushes.White, FontSize = 11.5, FontWeight = FontWeights.Medium },
        });
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)), Opacity = 0 };
        overlay.Children.Add(new Ico { Data = (Geometry)FindResource("IcoPlay"), FillShape = true, Width = 34, Height = 34, Foreground = Brushes.White });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6) };
        Button Act(string icon, string tip, Action a)
        {
            var b = new Button { Style = (Style)FindResource("IconBtn"), Width = 30, Height = 30, ToolTip = Loc.T(tip), Foreground = Brushes.White, Content = new Ico { Data = (Geometry)FindResource(icon), Width = 16, Height = 16 } };
            b.Click += (_, e) => { a(); e.Handled = true; };
            b.PreviewMouseLeftButtonUp += (_, e) => e.Handled = false;
            return b;
        }
        actions.Children.Add(Act("IcoFolder", "takes.show", () => Core.Shell.Reveal(t.Path)));
        actions.Children.Add(Act("IcoTrash", "takes.delete", () => { if (Core.Shell.Recycle(t.Path)) { _studio.Forget(t); BuildTakes(); } }));
        overlay.Children.Add(actions);
        inner.Children.Add(overlay);
        frame.Child = inner;
        card.Children.Add(frame);
        var text = new StackPanel { Margin = new Thickness(2, 8, 2, 0) };
        Grid.SetRow(text, 1);
        text.Children.Add(new TextBlock { Text = Format.When(t.When), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = new TextBlock { Text = $"{Format.Size(t.Bytes)} · {t.Width} × {t.Height} · {t.Fps} fps", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        text.Children.Add(sub);
        card.Children.Add(text);
        card.ToolTip = Path.GetFileName(t.Path);
        card.MouseEnter += (_, _) => Springs.To(overlay, OpacityProperty, 1, 300, 1);
        card.MouseLeave += (_, _) => Springs.To(overlay, OpacityProperty, 0, 300, 1);
        frame.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) == null) Core.Shell.Open(t.Path); };
        var menu = new ContextMenu();
        void Item(string key, Action a) { var mi = new MenuItem { Header = Loc.T(key) }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("takes.play", () => Core.Shell.Open(t.Path));
        Item("takes.show", () => Core.Shell.Reveal(t.Path));
        Item("takes.copy", () => { var c = new System.Collections.Specialized.StringCollection { t.Path }; Clipboard.SetFileDropList(c); });
        menu.Items.Add(new Separator());
        Item("takes.delete", () => { if (Core.Shell.Recycle(t.Path)) { _studio.Forget(t); BuildTakes(); } });
        card.ContextMenu = menu;
        return card;
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (x is T t) return t;
        return null;
    }

    // ── demo hooks (README pictures) ────────────────────────────────────────
    public void SettingsPage(string page) { Page("settings"); _settings?.Open(page); }
    public void DemoHover(bool on) { _hover = on; ApplyIris(); }
    public void DemoCount(int n) => OnCount(n);
    public void DemoPage(string page) => Page(page);
    public void DemoMode(string mode) { S.Source = mode; ShowMode(); Refresh(); }
    public void DemoTile(int i, string what)
    {
        if (i >= _tiles.Count) return;
        if (what == "pick") _tiles[i].Pick(); else _tiles[i].DemoHover(what == "hover");
    }

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
    static IFormatProvider Culture => Loc.Turkish ? new System.Globalization.CultureInfo("tr-TR") : System.Globalization.CultureInfo.InvariantCulture;

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Format(Culture, "{0:0.0} GB", bytes / (double)(1L << 30)),
        >= 1L << 20 => $"{bytes / (1L << 20)} MB",
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

/// <summary>Small icons of other apps' windows, for the window tiles.</summary>
public static class AppIcons
{
    static readonly Dictionary<uint, ImageSource?> Cache = [];
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

    public static ImageSource? For(IntPtr hwnd, uint pid)
    {
        if (Cache.TryGetValue(pid, out var img)) return img;
        SendMessageTimeout(hwnd, 0x007F, (IntPtr)1, IntPtr.Zero, 0x2, 50, out IntPtr icon); // WM_GETICON ICON_BIG
        if (icon == IntPtr.Zero) SendMessageTimeout(hwnd, 0x007F, (IntPtr)2, IntPtr.Zero, 0x2, 50, out icon);
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(hwnd, -14); // GCLP_HICON
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(hwnd, -34);
        try
        {
            if (icon != IntPtr.Zero)
            {
                img = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(48, 48));
                img.Freeze();
            }
        }
        catch { img = null; }
        return Cache[pid] = img;
    }
}
