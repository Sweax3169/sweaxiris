using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SweaxIris.Core;

namespace SweaxIris.UI;

/// <summary>
/// A volume fader whose track is a live level meter: the bar shows how loud the source is after the
/// volume, the knob sets the volume (0–200 %, with a soft click at 100 %). Steps on the spring clock.
/// </summary>
public sealed class Fader : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Fader),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public Func<float>? Level { get; set; }
    public bool Off { get => _off; set { _off = value; InvalidateVisual(); } }
    public event Action<double>? Changed;
    public event Action? Released;

    bool _off, _running, _drag;
    double _level, _peak, _hold, _knob = 0.5;

    public Fader()
    {
        Height = 28;
        Cursor = Cursors.Hand;
        IsVisibleChanged += (_, _) => Hook();
        Loaded += (_, _) => Hook();
        Theme.Changed += InvalidateVisual;
    }

    void Hook()
    {
        if (IsVisible && IsLoaded && !_running) { _running = true; Springs.Run(Step); }
    }

    bool Step(double dt)
    {
        if (!IsVisible || !IsLoaded) { _running = false; return false; }
        float raw = _off ? 0 : Level?.Invoke() ?? 0;
        double db = raw > 1e-5 ? 20 * Math.Log10(raw) : -90;
        double target = Math.Clamp((db + 54) / 54, 0, 1);
        _level = target > _level ? target : Math.Max(target, _level - dt * 1.3);
        if (_level >= _peak) { _peak = _level; _hold = 0.8; }
        else if ((_hold -= dt) < 0) _peak = Math.Max(_level, _peak - dt * 0.5);
        // the knob glides to its value
        double want = Math.Clamp(Value / 2, 0, 1);
        _knob += (want - _knob) * Math.Min(1, dt * (_drag ? 60 : 14));
        InvalidateVisual();
        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Application.Current.Resources["Sunken"] is not SolidColorBrush) return;
        double w = ActualWidth, h = ActualHeight, th = 8, y = (h - th) / 2;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        dc.DrawRoundedRectangle(Theme.Brush("Sunken"), null, new Rect(0, y, w, th), 4, 4);
        if (!_off && _level > 0.002)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), MappingMode = BrushMappingMode.Absolute };
            g.StartPoint = new Point(0, 0); g.EndPoint = new Point(w, 0);
            g.GradientStops.Add(new GradientStop(Theme.ColorOf("MeterLow"), 0));
            g.GradientStops.Add(new GradientStop(Theme.ColorOf("MeterLow"), 0.68));
            g.GradientStops.Add(new GradientStop(Theme.ColorOf("MeterMid"), 0.82));
            g.GradientStops.Add(new GradientStop(Theme.ColorOf("MeterHigh"), 0.97));
            dc.PushClip(new RectangleGeometry(new Rect(0, y, w, th), 4, 4));
            dc.DrawRectangle(g, null, new Rect(0, y, w * _level, th));
            dc.DrawRectangle(g, null, new Rect(Math.Max(0, w * _peak - 2), y, 2, th));
            dc.Pop();
        }
        // 100 % mark
        dc.DrawRectangle(Theme.Brush("LineStrong"), null, new Rect(w / 2 - 0.5, y - 4, 1, th + 8));
        // the knob
        double x = Math.Clamp(_knob * w, 8, w - 8);
        var knob = new Rect(x - 7, h / 2 - 9, 14, 18);
        dc.DrawRoundedRectangle(Theme.Brush("Raised"), new Pen(Theme.Brush(_off ? "LineStrong" : "Accent"), 2), knob, 5, 5);
        dc.DrawRectangle(Theme.Brush(_off ? "TextFaint" : "Accent"), null, new Rect(x - 0.75, h / 2 - 4, 1.5, 8));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _drag = true;
        CaptureMouse();
        Set(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag) Set(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_drag) return;
        _drag = false;
        ReleaseMouseCapture();
        Released?.Invoke();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value = Math.Round(Math.Clamp(Value + Math.Sign(e.Delta) * 0.05, 0, 2), 2);
        Changed?.Invoke(Value);
        Released?.Invoke();
        e.Handled = true;
    }

    void Set(double x)
    {
        double v = Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * 2;
        if (Math.Abs(v - 1) < 0.05) v = 1; // a soft detent at 100 %
        Value = Math.Round(v, 2);
        Changed?.Invoke(Value);
    }
}

/// <summary>
/// One thing that can be recorded: a live picture in a viewfinder with its name underneath. Hovering brings in
/// the viewfinder corners; choosing it pulls focus (the picture sharpens) and lights its tally.
/// </summary>
public sealed class SourceTile : Grid
{
    readonly Border _frame = new() { CornerRadius = new CornerRadius(10), ClipToBounds = true, BorderThickness = new Thickness(1) };
    readonly Image _image = new() { Stretch = Stretch.Uniform };
    readonly Grid _placeholder = new();
    readonly System.Windows.Shapes.Path _corners = new() { StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false, Opacity = 0 };
    readonly Border _tally = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(15), Opacity = 0, IsHitTestVisible = false };
    readonly BlurEffect _blur = new() { Radius = 0, KernelType = KernelType.Gaussian };
    readonly ScaleTransform _scale = new(1, 1);
    readonly TextBlock _title = new() { FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 8, 2, 0) };
    readonly TextBlock _sub = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 1, 2, 0) };
    bool _selected;

    public object? Key { get; init; }
    /// <summary>The live-picture key of this tile, if it has one.</summary>
    public string? PictureKey { get; set; }
    public event Action<SourceTile>? Picked;

    public SourceTile(double width, string title, string sub, ImageSource? icon = null, UIElement? placeholder = null)
    {
        Width = width;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(width * 9 / 16) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RenderTransform = _scale;
        RenderTransformOrigin = new Point(0.5, 0.5);

        _frame.SetResourceReference(Border.BackgroundProperty, "Viewfinder");
        _frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        var inner = new Grid();
        _image.Effect = _blur;
        inner.Children.Add(_image);
        if (placeholder != null) _placeholder.Children.Add(placeholder);
        else if (icon != null) _placeholder.Children.Add(new Image { Source = icon, Width = 40, Height = 40 });
        inner.Children.Add(_placeholder);
        _frame.Child = inner;
        Children.Add(_frame);

        // viewfinder corners, drawn just inside the frame
        double w = width, h = width * 9 / 16, k = 18, m = 7;
        _corners.Data = Geometry.Parse(FormattableString.Invariant(
            $"M{m},{m + k} V{m} H{m + k} M{w - m - k},{m} H{w - m} V{m + k} M{w - m},{h - m - k} V{h - m} H{w - m - k} M{m + k},{h - m} H{m} V{h - m - k}"));
        _corners.Stroke = Brushes.White;
        Children.Add(_corners);

        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentText");
        var chosen = new TextBlock { Text = Loc.T("tile.chosen"), FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        chosen.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        var tp = new StackPanel { Orientation = Orientation.Horizontal };
        tp.Children.Add(dot);
        tp.Children.Add(chosen);
        _tally.Child = tp;
        _tally.SetResourceReference(Border.BackgroundProperty, "Accent");
        Children.Add(_tally);

        _title.Text = title;
        _title.FontWeight = FontWeights.SemiBold;
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _sub.Text = sub;
        _sub.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        SetRow(_title, 1);
        SetRow(_sub, 2);
        Children.Add(_title);
        Children.Add(_sub);

        MouseEnter += (_, _) => Hover(true);
        MouseLeave += (_, _) => Hover(false);
        MouseLeftButtonDown += (_, e) => { Springs.To(_scale, ScaleTransform.ScaleXProperty, 0.97, 700, 0.9); Springs.To(_scale, ScaleTransform.ScaleYProperty, 0.97, 700, 0.9); e.Handled = true; };
        MouseLeftButtonUp += (_, _) => { Hover(IsMouseOver); Picked?.Invoke(this); };
    }

    public string Title { set => _title.Text = value; }
    public void Pick() => Picked?.Invoke(this);
    public ImageSource? Picture => _image.Source;
    public void DemoHover(bool on) => Hover(on);
    public string Sub { set => _sub.Text = value; }

    public void SetPicture(ImageSource? img, bool fade = true)
    {
        bool first = fade && _image.Source == null && img != null;
        _image.Source = img;
        _placeholder.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
        if (first) { _image.Opacity = 0; Springs.To(_image, OpacityProperty, 1, 220, 1); }
    }

    void Hover(bool on)
    {
        double s = on ? 1.025 : 1;
        Springs.To(_scale, ScaleTransform.ScaleXProperty, s, 420, 0.62);
        Springs.To(_scale, ScaleTransform.ScaleYProperty, s, 420, 0.62);
        if (!_selected) Springs.To(_corners, OpacityProperty, on ? 0.9 : 0, 360, 1);
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            _corners.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, value ? "Accent" : "Text");
            if (!value) _corners.Stroke = Brushes.White;
            Springs.To(_corners, OpacityProperty, value ? 1 : IsMouseOver ? 0.9 : 0, 360, 1);
            Springs.To(_tally, OpacityProperty, value ? 1 : 0, 360, 1);
            _frame.SetResourceReference(Border.BorderBrushProperty, value ? "Accent" : "Line");
            _frame.BorderThickness = new Thickness(value ? 2 : 1);
            // pulling focus: the picture sharpens as it is chosen (not when a tile is built already chosen)
            if (value && IsLoaded) Springs.To(_blur, BlurEffect.RadiusProperty, 0, 160, 0.9, 14);
        }
    }
}

/// <summary>
/// The monitors as Windows arranges them, drawn to scale: which one is left, right, above, portrait. Clicking one
/// chooses it, same as its picture.
/// </summary>
public sealed class MonitorMap : Grid
{
    public MonitorMap(IReadOnlyList<Recording.MonitorItem> monitors, string chosen, Action<string> pick, double width, double height = 104)
    {
        Width = width;
        Height = height;
        if (monitors.Count == 0) return;
        double minX = monitors.Min(m => m.Bounds.Left), minY = monitors.Min(m => m.Bounds.Top);
        double maxX = monitors.Max(m => m.Bounds.Right), maxY = monitors.Max(m => m.Bounds.Bottom);
        double k = Math.Min(width / (maxX - minX), height / (maxY - minY));
        double ox = (width - (maxX - minX) * k) / 2, oy = (height - (maxY - minY) * k) / 2;
        var canvas = new Canvas();
        Children.Add(canvas);
        foreach (var m in monitors)
        {
            bool on = m.Device.Equals(chosen, StringComparison.OrdinalIgnoreCase);
            var b = new Border
            {
                Width = Math.Max(8, m.Bounds.Width * k - 4), Height = Math.Max(8, m.Bounds.Height * k - 4),
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(on ? 2 : 1), Cursor = Cursors.Hand,
                ToolTip = $"{m.Title} · {m.Bounds.Width} × {m.Bounds.Height}",
            };
            b.SetResourceReference(Border.BackgroundProperty, on ? "AccentSoft" : "Raised");
            b.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "Line");
            var label = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var num = new TextBlock { Text = m.Number.ToString(), FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            num.SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "Text");
            label.Children.Add(num);
            if (b.Width > 70)
            {
                var name = new TextBlock { Text = m.Name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = b.Width - 10 };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
                label.Children.Add(name);
            }
            b.Child = label;
            var dev = m.Device;
            b.MouseEnter += (_, _) => { if (!on) b.SetResourceReference(Border.BorderBrushProperty, "LineStrong"); };
            b.MouseLeave += (_, _) => { if (!on) b.SetResourceReference(Border.BorderBrushProperty, "Line"); };
            b.MouseLeftButtonUp += (_, _) => pick(dev);
            Canvas.SetLeft(b, ox + (m.Bounds.Left - minX) * k + 2);
            Canvas.SetTop(b, oy + (m.Bounds.Top - minY) * k + 2);
            canvas.Children.Add(b);
        }
    }
}
