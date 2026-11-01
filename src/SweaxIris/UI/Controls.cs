using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SweaxIris.Core;

namespace SweaxIris.UI;

/// <summary>Buttons sink a little under the finger and spring back.</summary>
public static class Press
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(Press),
        new PropertyMetadata(false, (d, e) =>
        {
            if (d is not UIElement el || !(bool)e.NewValue) return;
            var s = new ScaleTransform(1, 1);
            el.RenderTransform = s;
            el.PreviewMouseLeftButtonDown += (_, _) => { Springs.To(s, ScaleTransform.ScaleXProperty, 0.955, 900, 0.9); Springs.To(s, ScaleTransform.ScaleYProperty, 0.955, 900, 0.9); };
            MouseEventHandler up = (_, _) => { Springs.To(s, ScaleTransform.ScaleXProperty, 1, 520, 0.55); Springs.To(s, ScaleTransform.ScaleYProperty, 1, 520, 0.55); };
            el.PreviewMouseLeftButtonUp += (o, a) => up(o, a);
            el.MouseLeave += up;
        }));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);
}

/// <summary>A stroked 24×24 line icon in the current text colour.</summary>
public sealed class Ico : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Ico),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Ico),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(double), typeof(Ico),
        new FrameworkPropertyMetadata(1.7, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillShapeProperty = DependencyProperty.Register(nameof(FillShape), typeof(bool), typeof(Ico),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Stroke { get => (double)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public bool FillShape { get => (bool)GetValue(FillShapeProperty); set => SetValue(FillShapeProperty, value); }

    public Ico() { Width = 18; Height = 18; SnapsToDevicePixels = true; }

    protected override void OnRender(DrawingContext dc)
    {
        if (Data == null) return;
        double s = Math.Min(ActualWidth, ActualHeight) / 24;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * s) / 2, (ActualHeight - 24 * s) / 2));
        dc.PushTransform(new ScaleTransform(s, s));
        var pen = new Pen(Foreground, Stroke / s * Math.Max(1, s * 0.9)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(FillShape ? Foreground : null, pen, Data);
        dc.Pop();
        dc.Pop();
    }
}

/// <summary>An on/off switch: a soft square track with a knob that springs across.</summary>
public sealed class Toggle : ToggleButton
{
    double _p;
    readonly ScaleTransform _squash = new(1, 1);

    public Toggle()
    {
        Width = 38; Height = 22;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        Template = null;
        Theme.Changed += InvalidateVisual;
        Loaded += (_, _) => { _p = IsChecked == true ? 1 : 0; InvalidateVisual(); };
    }

    static readonly DependencyProperty PProperty = DependencyProperty.Register("P", typeof(double), typeof(Toggle),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((Toggle)d)._p = (double)e.NewValue));

    protected override void OnChecked(RoutedEventArgs e) { base.OnChecked(e); Springs.To(this, PProperty, 1, 420, 0.62, _p); }
    protected override void OnUnchecked(RoutedEventArgs e) { base.OnUnchecked(e); Springs.To(this, PProperty, 0, 420, 0.62, _p); }
    protected override Size MeasureOverride(Size constraint) => new(Width, Height);
    protected override Size ArrangeOverride(Size arrangeBounds) => arrangeBounds;

    protected override void OnRender(DrawingContext dc)
    {
        if (Application.Current.Resources["Accent"] is not SolidColorBrush accent) return;
        double w = ActualWidth, h = ActualHeight, p = Math.Clamp(_p, -0.1, 1.1);
        var off = Theme.ColorOf("Sunken");
        var on = accent.Color;
        var t = Math.Clamp(p, 0, 1);
        var track = Color.FromRgb((byte)(off.R + (on.R - off.R) * t), (byte)(off.G + (on.G - off.G) * t), (byte)(off.B + (on.B - off.B) * t));
        dc.DrawRoundedRectangle(new SolidColorBrush(track), new Pen(Theme.Brush(t > 0.5 ? "Accent" : "LineStrong"), 1), new Rect(0.5, 0.5, w - 1, h - 1), 7, 7);
        double k = h - 8;
        double x = 4 + (w - 8 - k) * p;
        var knob = t > 0.5 ? Theme.Brush("AccentText") : Theme.Brush("TextDim");
        dc.DrawRoundedRectangle(knob, null, new Rect(x, 4, k, k), 4.5, 4.5);
        if (IsMouseOver) dc.DrawRoundedRectangle(Theme.Brush("Hover"), null, new Rect(0, 0, w, h), 7, 7);
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }
}

/// <summary>A row of choices with a highlight that glides to the picked one.</summary>
public sealed class Segments : Border
{
    readonly Grid _grid = new();
    readonly Border _mark = new() { CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left };
    readonly TranslateTransform _move = new();
    readonly List<(object Value, TextBlock Text, Border Hit)> _items = [];
    object? _value;

    public event Action<object>? Changed;

    public Segments()
    {
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(3);
        SetResourceReference(BackgroundProperty, "Sunken");
        _mark.SetResourceReference(Border.BackgroundProperty, "Raised");
        _mark.SetResourceReference(Border.BorderBrushProperty, "Line");
        _mark.BorderThickness = new Thickness(1);
        _mark.RenderTransform = _move;
        _mark.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.18, Color = Colors.Black };
        var host = new Grid();
        host.Children.Add(_mark);
        host.Children.Add(_grid);
        Child = host;
        SizeChanged += (_, _) => Place(false);
        Focusable = true;
        FocusVisualStyle = null;
    }

    public object? Value
    {
        get => _value;
        set { _value = value; Restyle(); Place(IsLoaded); }
    }

    public void SetItems(IEnumerable<(object Value, string Label)> items)
    {
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _items.Clear();
        int i = 0;
        foreach (var (value, label) in items)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var text = new TextBlock
            {
                Text = label, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.5, FontFamily = (FontFamily)FindResource("PlexMedium"), Margin = new Thickness(8, 6, 8, 6), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var hit = new Border { Background = Brushes.Transparent, Child = text, Cursor = Cursors.Hand };
            var v = value;
            hit.MouseLeftButtonUp += (_, _) => Pick(v);
            Grid.SetColumn(hit, i++);
            _grid.Children.Add(hit);
            _items.Add((value, text, hit));
        }
        Restyle();
        Place(false);
    }

    void Pick(object v)
    {
        if (Equals(v, _value)) return;
        _value = v;
        Restyle();
        Place(true);
        Changed?.Invoke(v);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int i = _items.FindIndex(x => Equals(x.Value, _value));
        if (e.Key == Key.Right && i < _items.Count - 1) { Pick(_items[i + 1].Value); e.Handled = true; }
        if (e.Key == Key.Left && i > 0) { Pick(_items[i - 1].Value); e.Handled = true; }
    }

    void Restyle()
    {
        foreach (var (value, text, _) in _items)
            text.SetResourceReference(TextBlock.ForegroundProperty, Equals(value, _value) ? "Text" : "TextDim");
    }

    void Place(bool animate)
    {
        int i = _items.FindIndex(x => Equals(x.Value, _value));
        _mark.Visibility = i < 0 ? Visibility.Hidden : Visibility.Visible;
        if (i < 0 || _grid.ActualWidth <= 0) return;
        double w = _grid.ActualWidth / _items.Count;
        if (animate)
        {
            Springs.To(_move, TranslateTransform.XProperty, w * i, 380, 0.78);
            Springs.To(_mark, WidthProperty, w, 380, 0.78, double.IsNaN(_mark.Width) ? w : null);
        }
        else
        {
            Springs.Set(_move, TranslateTransform.XProperty, w * i);
            Springs.Set(_mark, WidthProperty, w);
        }
    }
}

/// <summary>A segmented level meter in decibels with a falling peak marker; polls its source while visible.</summary>
public sealed class LevelMeter : FrameworkElement
{
    public Func<float>? Source { get; set; }
    public bool Muted { get; set; }
    double _level, _peak, _peakHold;
    bool _running;
    const int Segs = 26;

    public LevelMeter()
    {
        Height = 6;
        IsVisibleChanged += (_, _) => Hook();
        Loaded += (_, _) => Hook();
    }

    // runs on the shared spring clock, so demo recordings step it frame by frame like everything else
    void Hook()
    {
        if (IsVisible && IsLoaded && !_running)
        {
            _running = true;
            Springs.Run(Step);
        }
    }

    bool Step(double dt)
    {
        if (!IsVisible || !IsLoaded) { _running = false; return false; }
        float raw = Source?.Invoke() ?? 0;
        double db = raw > 1e-5 ? 20 * Math.Log10(raw) : -90;
        double target = Math.Clamp((db + 60) / 60, 0, 1);
        // fast attack, slow release, like a broadcast meter
        _level = target > _level ? target : Math.Max(target, _level - dt * 1.4);
        if (_level >= _peak) { _peak = _level; _peakHold = 0.9; }
        else if ((_peakHold -= dt) < 0) _peak = Math.Max(_level, _peak - dt * 0.6);
        InvalidateVisual();
        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Application.Current.Resources["Sunken"] is not SolidColorBrush) return;
        double w = ActualWidth, h = ActualHeight;
        double gap = 2, sw = (w - gap * (Segs - 1)) / Segs;
        if (sw <= 0) return;
        var off = Theme.Brush("Sunken");
        var low = Theme.Brush(Muted ? "TextFaint" : "MeterLow");
        var mid = Theme.Brush(Muted ? "TextFaint" : "MeterMid");
        var high = Theme.Brush(Muted ? "TextFaint" : "MeterHigh");
        int lit = (int)Math.Round(_level * Segs);
        int peak = Math.Min(Segs - 1, (int)Math.Round(_peak * Segs) - 1);
        for (int i = 0; i < Segs; i++)
        {
            double pos = (double)i / Segs;
            var b = i < lit || i == peak ? (pos > 0.88 ? high : pos > 0.7 ? mid : low) : off;
            dc.DrawRoundedRectangle(b, null, new Rect(i * (sw + gap), 0, sw, h), 1.5, 1.5);
        }
    }
}

/// <summary>A shortcut drawn as key caps: [Ctrl] [Alt] [R].</summary>
public static class KeyCaps
{
    public static Panel Build(Hotkey? hk, double size = 12, string? none = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (hk == null)
        {
            var t = new TextBlock { Text = none ?? Loc.T("key.none"), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
            row.Children.Add(t);
            return row;
        }
        bool first = true;
        foreach (var part in hk.Value.Parts)
        {
            if (!first) row.Children.Add(new FrameworkElement { Width = 4 });
            first = false;
            var cap = new Border
            {
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(6, 1, 6, 1), MinWidth = size * 1.9,
                Child = new TextBlock { Text = part, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center },
            };
            cap.SetResourceReference(Border.BackgroundProperty, "Raised");
            cap.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
            ((TextBlock)cap.Child).SetResourceReference(TextBlock.ForegroundProperty, "Text");
            ((TextBlock)cap.Child).SetResourceReference(TextBlock.FontFamilyProperty, "PlexMedium");
            row.Children.Add(cap);
        }
        return row;
    }
}
