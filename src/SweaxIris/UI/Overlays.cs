using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SweaxIris.Core;
using SweaxIris.Native;
using SweaxIris.Recording;

namespace SweaxIris.UI;

/// <summary>Base for the small always-on-top windows: no focus stealing, kept out of every recording.</summary>
public class FloatWindow : Window
{
    readonly bool _clickThrough;
    public IntPtr Handle { get; private set; }

    public FloatWindow(bool clickThrough = false)
    {
        _clickThrough = clickThrough;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)Application.Current.FindResource("Plex");
        UseLayoutRounding = true;
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            long ex = (long)Win32.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE) | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            if (_clickThrough) ex |= Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED;
            Win32.SetWindowLongPtr(Handle, Win32.GWL_EXSTYLE, (IntPtr)ex);
            Win32.SetWindowDisplayAffinity(Handle, Win32.WDA_EXCLUDEFROMCAPTURE);
        };
    }

    /// <summary>Places the window at desktop pixel (x, y) (top-left), whatever the monitor's DPI.</summary>
    public void PlaceAt(int x, int y)
    {
        if (Handle == IntPtr.Zero) return;
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    /// <summary>The window's size in physical pixels.</summary>
    public (int W, int H) PixelSize
    {
        get
        {
            if (Handle == IntPtr.Zero) return (0, 0);
            Win32.GetWindowRect(Handle, out var r);
            return (r.Width, r.Height);
        }
    }
}

/// <summary>3 · 2 · 1 over the middle of what will be recorded, as a lens stopping down each second.</summary>
public sealed class CountdownWindow : FloatWindow
{
    readonly Iris _iris = new() { Width = 168, Height = 168, Aperture = 0.5 };
    readonly TextBlock _num = new() { FontSize = 54, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Win32.RECT _area;

    public CountdownWindow(Win32.RECT area) : base(clickThrough: true)
    {
        _area = area;
        _num.FontFamily = (FontFamily)FindResource("Plex"); _num.FontWeight = FontWeights.SemiBold;
        _num.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.7 };
        _iris.Core = (Color)FindResource("TallyColor");
        var g = new Grid { Width = 200, Height = 200 };
        var disc = new Border { CornerRadius = new CornerRadius(100), Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) };
        g.Children.Add(disc);
        g.Children.Add(_iris);
        g.Children.Add(_num);
        Content = g;
        Loaded += (_, _) =>
        {
            var (w, h) = PixelSize;
            PlaceAt((_area.Left + _area.Right - w) / 2, (_area.Top + _area.Bottom - h) / 2);
            Opacity = 0;
            Springs.To(this, OpacityProperty, 1, 400, 1);
        };
    }

    public void Show(int n)
    {
        if (!IsVisible) base.Show();
        _num.Text = n.ToString();
        var s = new ScaleTransform(1.25, 1.25);
        _num.RenderTransform = s;
        _num.RenderTransformOrigin = new Point(0.5, 0.5);
        Springs.To(s, ScaleTransform.ScaleXProperty, 1, 420, 0.55);
        Springs.To(s, ScaleTransform.ScaleYProperty, 1, 420, 0.55);
        Springs.To(_iris, Iris.ApertureProperty, 0.62, 260, 0.62, 0.08);
        Springs.To(_iris, Iris.TurnProperty, _iris.Turn + 51.4, 200, 0.85);
    }

    public void Go()
    {
        // the lens snaps open as recording starts, then the window fades
        Springs.To(_iris, Iris.ApertureProperty, 1.05, 300, 0.7);
        _num.Text = "";
        Springs.To(this, OpacityProperty, 0, 220, 1);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }
}

/// <summary>A thin frame around the recorded region (outside it, never in the video).</summary>
public sealed class RegionFrame : FloatWindow
{
    readonly Win32.RECT _r;
    readonly FrameShape _shape = new();
    readonly DispatcherTimer _march = new() { Interval = TimeSpan.FromMilliseconds(50) };

    public RegionFrame(Win32.RECT r) : base(clickThrough: true)
    {
        _r = r;
        SizeToContent = SizeToContent.Manual;
        Content = _shape;
        Loaded += (_, _) =>
        {
            const int m = 5;
            Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, _r.Left - m, _r.Top - m, _r.Width + 2 * m, _r.Height + 2 * m, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            Opacity = 0;
            Springs.To(this, OpacityProperty, 1, 300, 1);
        };
        _march.Tick += (_, _) => { _shape.Phase += 0.6; _shape.InvalidateVisual(); };
        _march.Start();
        Closed += (_, _) => _march.Stop();
    }

    public void SetPaused(bool paused) { _shape.Paused = paused; _shape.InvalidateVisual(); }

    sealed class FrameShape : FrameworkElement
    {
        public double Phase;
        public bool Paused;

        protected override void OnRender(DrawingContext dc)
        {
            var c = (Color)FindResource(Paused ? "AccentColor" : "TallyColor");
            var r = new Rect(1.5, 1.5, ActualWidth - 3, ActualHeight - 3);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x50, c.R, c.G, c.B)), 3), r);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(c), 2) { DashStyle = new DashStyle([6, 4], Phase) }, r);
        }
    }
}

/// <summary>
/// The recording bar: a breathing tally light, the running time, the sound levels and pause / mic / stop.
/// It sits beside what is being recorded and can be dragged anywhere.
/// </summary>
public sealed class ControllerWindow : FloatWindow
{
    readonly Studio _studio;
    readonly Iris _tally = new() { Width = 26, Height = 26, ShowScale = false, Aperture = 0.8 };
    readonly TextBlock _time = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Width = 76 };
    readonly TextBlock _state = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    readonly LevelMeter _sys = new() { Width = 64, Height = 4 };
    readonly LevelMeter _mic = new() { Width = 64, Height = 4 };
    readonly Ico _pauseIco = new() { Width = 16, Height = 16, Stroke = 2.2 };
    readonly Ico _micIco = new() { Width = 16, Height = 16 };
    readonly Button _micBtn;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    double _t;
    bool _placed;

    public Action? PauseClicked, MicClicked, StopClicked;

    public ControllerWindow(Studio studio)
    {
        _studio = studio;
        _time.FontFamily = (FontFamily)FindResource("Plex"); _time.FontWeight = FontWeights.Medium;
        _time.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _state.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        _tally.Core = (Color)FindResource("TallyColor");
        _sys.Source = () => Math.Max(0, _studio.SystemPeak);
        _mic.Source = () => Math.Max(0, _studio.MicPeak);

        Button B(Ico ico, string tip, Action click)
        {
            var b = new Button { Style = (Style)FindResource("IconBtn"), Width = 32, Height = 32, Content = ico, ToolTip = tip };
            b.SetResourceReference(ForegroundProperty, "Text");
            b.Click += (_, _) => click();
            return b;
        }
        var stopIco = new Ico { Data = (Geometry)FindResource("IcoStop"), Width = 15, Height = 15, FillShape = true };
        stopIco.SetResourceReference(Ico.ForegroundProperty, "Tally");
        _micBtn = B(_micIco, Loc.T("key.mic"), () => MicClicked?.Invoke());

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_tally);
        var times = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        times.Children.Add(_time);
        times.Children.Add(_state);
        row.Children.Add(times);
        var meters = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        meters.Children.Add(_sys);
        meters.Children.Add(new FrameworkElement { Height = 5 });
        meters.Children.Add(_mic);
        row.Children.Add(meters);
        var sep = new Border { Width = 1, Height = 22, Margin = new Thickness(2, 0, 4, 0) };
        sep.SetResourceReference(Border.BackgroundProperty, "Line");
        row.Children.Add(sep);
        row.Children.Add(B(_pauseIco, Loc.T("key.pause"), () => PauseClicked?.Invoke()));
        row.Children.Add(_micBtn);
        row.Children.Add(B(stopIco, Loc.T("stop"), () => StopClicked?.Invoke()));

        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 7, 6, 7), Child = row, BorderThickness = new Thickness(1), Margin = new Thickness(14, 10, 14, 18), Cursor = Cursors.SizeAll };
        card.SetResourceReference(Border.BackgroundProperty, "Raised");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.35 };
        card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) DragMove(); };
        Content = card;

        _tick.Tick += (_, _) => { if (!Studio.Demo) Tick(); };
        Loaded += (_, _) => { Refresh(); _tick.Start(); EnterAnimation(); };
        Closed += (_, _) => _tick.Stop();
    }

    void EnterAnimation()
    {
        var s = new ScaleTransform(0.85, 0.85);
        var c = (FrameworkElement)Content;
        c.RenderTransform = s;
        c.RenderTransformOrigin = new Point(0.5, 0.5);
        c.Opacity = 0;
        Springs.To(s, ScaleTransform.ScaleXProperty, 1, 420, 0.6);
        Springs.To(s, ScaleTransform.ScaleYProperty, 1, 420, 0.6);
        Springs.To(c, OpacityProperty, 1, 300, 1);
    }

    /// <summary>Puts the bar next to the target: under a region, at the top of a screen or window.</summary>
    public void Place(CaptureTarget t)
    {
        if (_placed) return;
        _placed = true;
        UpdateLayout();
        var (w, h) = PixelSize;
        var mon = Win32.MonitorInfo(t.Monitor != IntPtr.Zero ? t.Monitor : Win32.MonitorFromPoint(default, Win32.MONITOR_DEFAULTTOPRIMARY));
        var work = mon.rcWork;
        int x, y;
        if (t.Kind == SourceKind.Region)
        {
            var r = t.Region;
            x = (r.Left + r.Right - w) / 2;
            y = r.Bottom + 4 + h <= work.Bottom ? r.Bottom + 4 : r.Top - h - 4 >= work.Top ? r.Top - h - 4 : r.Bottom - h - 8;
        }
        else
        {
            var r = t.Kind == SourceKind.Window ? Win32.FrameOf(t.Window) : mon.rcMonitor;
            x = (r.Left + r.Right - w) / 2;
            y = Math.Max(work.Top, r.Top + 6);
        }
        x = Math.Clamp(x, work.Left, work.Right - w);
        y = Math.Clamp(y, work.Top, work.Bottom - h);
        PlaceAt(x, y);
    }

    public void Refresh()
    {
        bool paused = _studio.State == StudioState.Paused;
        _pauseIco.Data = (Geometry)FindResource(paused ? "IcoPlay" : "IcoPause");
        _pauseIco.FillShape = paused;
        _micIco.Data = (Geometry)FindResource(_studio.MicMuted ? "IcoMicOff" : "IcoMic");
        _micBtn.SetResourceReference(ForegroundProperty, _studio.MicMuted ? "Tally" : "Text");
        _micBtn.Visibility = _studio.S.Microphone ? Visibility.Visible : Visibility.Collapsed;
        _mic.Visibility = _studio.S.Microphone ? Visibility.Visible : Visibility.Collapsed;
        _mic.Muted = _studio.MicMuted;
        _state.Text = paused ? Loc.T("paused") : Loc.T("recording");
        _tally.Core = (Color)FindResource(paused ? "AccentColor" : "TallyColor");
        Springs.To(_tally, Iris.ApertureProperty, paused ? 0.4 : 0.8, 300, 0.6);
    }

    public void DemoTick() => Tick();

    void Tick()
    {
        if (_studio.State is StudioState.Recording or StudioState.Paused)
        {
            var e = _studio.Elapsed;
            _time.Text = $"{(int)e.TotalHours:00}:{e.Minutes:00}:{e.Seconds:00}";
        }
        _t += 0.1;
        if (_studio.State == StudioState.Recording) _tally.Glow = 0.4 + 0.4 * Math.Cos(_t * 2.4);
        else _tally.Glow = 0;
    }

    public void Dismiss()
    {
        _tick.Stop();
        Springs.To(this, OpacityProperty, 0, 400, 1);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }
}

/// <summary>"Saved" with a picture of the take, bottom right, for a few seconds.</summary>
public sealed class ToastWindow : FloatWindow
{
    readonly DispatcherTimer _life = new() { Interval = TimeSpan.FromSeconds(7) };
    readonly TranslateTransform _slide = new(40, 0);

    public ToastWindow(Take take, string? message = null, bool error = false)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        var thumb = new Border { Width = 112, Height = 63, CornerRadius = new CornerRadius(7), ClipToBounds = true, Margin = new Thickness(0, 0, 12, 0) };
        thumb.SetResourceReference(Border.BackgroundProperty, "Viewfinder");
        if (take.Thumb != null && File.Exists(take.Thumb))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 224; bi.UriSource = new Uri(take.Thumb); bi.EndInit(); bi.Freeze();
                thumb.Child = new Image { Source = bi, Stretch = Stretch.UniformToFill };
            }
            catch { }
        }
        else
        {
            thumb.Child = new Iris { Width = 40, Height = 40, ShowScale = false, Aperture = error ? 0.05 : 0.5, Core = (Color)FindResource(error ? "TallyColor" : "AccentColor") };
        }
        row.Children.Add(thumb);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        var title = new TextBlock { FontFamily = (FontFamily)FindResource("Plex"), FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, error ? "Tally" : "Text");
        title.Text = error ? Loc.T("err.title") : $"{Loc.T("toast.saved")} · {Format.Duration(take.Seconds)} · {Format.Size(take.Bytes)}";
        var sub = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = error ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxHeight = 48 };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        sub.Text = message ?? Path.GetFileName(take.Path);
        text.Children.Add(title);
        text.Children.Add(sub);
        if (!error)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var play = new Button { Style = (Style)FindResource("BtnAccent"), Content = Loc.T("toast.play"), Padding = new Thickness(12, 4, 12, 4) };
            var folder = new Button { Style = (Style)FindResource("Btn"), Content = Loc.T("toast.folder"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
            play.Click += (_, _) => { Core.Shell.Open(take.Path); Dismiss(); };
            folder.Click += (_, _) => { Core.Shell.Reveal(take.Path); Dismiss(); };
            buttons.Children.Add(play);
            buttons.Children.Add(folder);
            text.Children.Add(buttons);
        }
        row.Children.Add(text);
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12), Child = row, BorderThickness = new Thickness(1), Margin = new Thickness(16, 12, 16, 20), RenderTransform = _slide };
        card.SetResourceReference(Border.BackgroundProperty, "Raised");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = 0.35 };
        Content = card;
        _life.Tick += (_, _) => Dismiss();
        MouseEnter += (_, _) => _life.Stop();
        MouseLeave += (_, _) => _life.Start();
        Loaded += (_, _) =>
        {
            var work = Win32.MonitorInfo(Win32.MonitorFromPoint(default, Win32.MONITOR_DEFAULTTOPRIMARY)).rcWork;
            var (w, h) = PixelSize;
            PlaceAt(work.Right - w - 8, work.Bottom - h - 8);
            Opacity = 0;
            Springs.To(this, OpacityProperty, 1, 300, 1);
            Springs.To(_slide, TranslateTransform.XProperty, 0, 340, 0.7);
            _life.Start();
        };
    }

    public void Dismiss()
    {
        _life.Stop();
        Springs.To(this, OpacityProperty, 0, 300, 1);
        Springs.To(_slide, TranslateTransform.XProperty, 40, 300, 1);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }
}
