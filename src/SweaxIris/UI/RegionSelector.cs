using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SweaxIris.Core;
using SweaxIris.Native;

namespace SweaxIris.UI;

/// <summary>
/// The region picker: every monitor gets a dim veil over the live screen. Drag to draw a rectangle (Shift keeps
/// 16:9), or click a window to take its frame; then move it, pull its handles or nudge it with the arrows, and
/// press Enter (or Record) to start. Esc cancels. All in physical pixels, so it is exact on mixed-DPI setups.
/// </summary>
public sealed class RegionSelector
{
    enum Phase { Hover, Drawing, Adjusting }
    enum Grip { None, Move, N, S, E, W, NE, NW, SE, SW }

    readonly List<SelectorWindow> _windows = [];
    readonly List<(Win32.RECT Rect, string Title)> _targets = [];
    readonly TaskCompletionSource<Win32.RECT?> _done = new();
    Phase _phase = Phase.Hover;
    Grip _grip;
    Win32.POINT _cursor, _anchor;
    Win32.RECT _sel, _start;
    Win32.RECT _bounds; // the monitor the selection lives on
    int _hover = -1;
    bool _finished;

    /// <summary>For the README pictures: the picker's look for a given moment, on a made-up screen.</summary>
    public static FrameworkElement DemoVisual(double w, double h, Rect sel, string phase, Point cursor, Rect? hover)
    {
        var s = new RegionSelector
        {
            _phase = phase switch { "draw" => Phase.Drawing, "adjust" => Phase.Adjusting, _ => Phase.Hover },
            _sel = R(sel), _cursor = new Win32.POINT { X = (int)cursor.X, Y = (int)cursor.Y },
            _bounds = new Win32.RECT { Right = (int)w, Bottom = (int)h },
        };
        if (hover is { } hr) { s._targets.Add((R(hr), "Atlas — Live dashboard")); s._hover = 0; }
        var win = new SelectorWindow(s, new Win32.RECT { Right = (int)w, Bottom = (int)h });
        return win.Detach(w, h);
    }

    static Win32.RECT R(Rect r) => r.IsEmpty ? default : new Win32.RECT { Left = (int)r.Left, Top = (int)r.Top, Right = (int)r.Right, Bottom = (int)r.Bottom };

    /// <summary>Shows the picker; returns the rectangle in desktop pixels, or null when cancelled.</summary>
    public static Task<Win32.RECT?> PickAsync(Win32.RECT? previous = null)
    {
        var s = new RegionSelector();
        s.Open(previous);
        return s._done.Task;
    }

    void Open(Win32.RECT? previous)
    {
        // windows on screen, top to bottom, for click-to-take
        var shell = Win32.GetShellWindow();
        foreach (var h in Win32.RealWindows())
            _targets.Add((Win32.FrameOf(h), Win32.GetWindowTitle(h)));
        Win32.GetCursorPos(out _cursor);
        foreach (var (h, r, _) in Win32.MonitorList())
        {
            var w = new SelectorWindow(this, r);
            _windows.Add(w);
            w.Show();
        }
        if (previous is { } p && p.Width > 8)
        {
            _sel = p;
            _bounds = Win32.MonitorInfo(Win32.MonitorFromRect(ref p, Win32.MONITOR_DEFAULTTONEAREST)).rcMonitor;
            _phase = Phase.Adjusting;
        }
        // focus the window under the mouse so the keys work at once
        var under = _windows.FirstOrDefault(w => Contains(w.Bounds, _cursor)) ?? _windows.FirstOrDefault();
        under?.Activate();
        if (under != null) Win32.ForceForeground(new WindowInteropHelper(under).Handle);
        UpdateHover();
        Redraw();
    }

    static bool Contains(Win32.RECT r, Win32.POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    static Win32.RECT Clamp(Win32.RECT r, Win32.RECT b) => new()
    {
        Left = Math.Max(r.Left, b.Left), Top = Math.Max(r.Top, b.Top), Right = Math.Min(r.Right, b.Right), Bottom = Math.Min(r.Bottom, b.Bottom),
    };

    void UpdateHover()
    {
        _hover = -1;
        if (_phase != Phase.Hover) return;
        for (int i = 0; i < _targets.Count; i++)
            if (Contains(_targets[i].Rect, _cursor)) { _hover = i; break; }
    }

    internal void Redraw() { foreach (var w in _windows) w.Paint(); }

    // ── input (in desktop pixels) ───────────────────────────────────────────

    internal void Down(Win32.POINT p, bool doubleClick)
    {
        _cursor = p;
        if (_phase == Phase.Adjusting)
        {
            if (doubleClick && Contains(_sel, p)) { Finish(true); return; }
            _grip = GripAt(p);
            if (_grip != Grip.None) { _anchor = p; _start = _sel; return; }
        }
        _phase = Phase.Drawing;
        _anchor = p;
        _bounds = Win32.MonitorInfo(Win32.MonitorFromPoint(p, Win32.MONITOR_DEFAULTTONEAREST)).rcMonitor;
        _sel = new Win32.RECT { Left = p.X, Top = p.Y, Right = p.X, Bottom = p.Y };
        Redraw();
    }

    internal void Move(Win32.POINT p, bool shift)
    {
        _cursor = p;
        if (_phase == Phase.Drawing)
        {
            int x0 = Math.Clamp(_anchor.X, _bounds.Left, _bounds.Right), y0 = Math.Clamp(_anchor.Y, _bounds.Top, _bounds.Bottom);
            int x1 = Math.Clamp(p.X, _bounds.Left, _bounds.Right), y1 = Math.Clamp(p.Y, _bounds.Top, _bounds.Bottom);
            if (shift)
            {
                // keep 16:9, led by the longer pull
                int w = Math.Abs(x1 - x0), h = Math.Abs(y1 - y0);
                if (w * 9 > h * 16) h = w * 9 / 16; else w = h * 16 / 9;
                x1 = x0 + Math.Sign(x1 - x0 == 0 ? 1 : x1 - x0) * w;
                y1 = y0 + Math.Sign(y1 - y0 == 0 ? 1 : y1 - y0) * h;
            }
            _sel = Clamp(new Win32.RECT { Left = Math.Min(x0, x1), Top = Math.Min(y0, y1), Right = Math.Max(x0, x1), Bottom = Math.Max(y0, y1) }, _bounds);
        }
        else if (_phase == Phase.Adjusting && _grip != Grip.None && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            int dx = p.X - _anchor.X, dy = p.Y - _anchor.Y;
            var r = _start;
            if (_grip == Grip.Move)
            {
                dx = Math.Clamp(dx, _bounds.Left - r.Left, _bounds.Right - r.Right);
                dy = Math.Clamp(dy, _bounds.Top - r.Top, _bounds.Bottom - r.Bottom);
                r.Left += dx; r.Right += dx; r.Top += dy; r.Bottom += dy;
            }
            else
            {
                if (_grip is Grip.W or Grip.NW or Grip.SW) r.Left = Math.Min(r.Right - 32, r.Left + dx);
                if (_grip is Grip.E or Grip.NE or Grip.SE) r.Right = Math.Max(r.Left + 32, r.Right + dx);
                if (_grip is Grip.N or Grip.NW or Grip.NE) r.Top = Math.Min(r.Bottom - 32, r.Top + dy);
                if (_grip is Grip.S or Grip.SW or Grip.SE) r.Bottom = Math.Max(r.Top + 32, r.Bottom + dy);
                r = Clamp(r, _bounds);
            }
            _sel = r;
        }
        else UpdateHover();
        Redraw();
    }

    internal void Up(Win32.POINT p)
    {
        _cursor = p;
        if (_phase == Phase.Drawing)
        {
            if (_sel.Width < 6 || _sel.Height < 6)
            {
                // a click: take the window under it (or the whole monitor over the desktop)
                _phase = Phase.Hover;
                UpdateHover();
                _bounds = Win32.MonitorInfo(Win32.MonitorFromPoint(p, Win32.MONITOR_DEFAULTTONEAREST)).rcMonitor;
                _sel = _hover >= 0 ? Clamp(_targets[_hover].Rect, _bounds) : _bounds;
            }
            if (_sel.Width < 32) _sel.Right = Math.Min(_bounds.Right, _sel.Left + 32);
            if (_sel.Height < 32) _sel.Bottom = Math.Min(_bounds.Bottom, _sel.Top + 32);
            _phase = Phase.Adjusting;
            _hover = -1;
        }
        _grip = Grip.None;
        Redraw();
    }

    internal void Key(Key key, bool shift)
    {
        if (key == System.Windows.Input.Key.Escape) { Finish(false); return; }
        if (_phase != Phase.Adjusting) return;
        if (key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { Finish(true); return; }
        int step = shift ? 10 : 1, dx = 0, dy = 0;
        if (key == System.Windows.Input.Key.Left) dx = -step;
        if (key == System.Windows.Input.Key.Right) dx = step;
        if (key == System.Windows.Input.Key.Up) dy = -step;
        if (key == System.Windows.Input.Key.Down) dy = step;
        if (dx == 0 && dy == 0) return;
        dx = Math.Clamp(dx, _bounds.Left - _sel.Left, _bounds.Right - _sel.Right);
        dy = Math.Clamp(dy, _bounds.Top - _sel.Top, _bounds.Bottom - _sel.Bottom);
        _sel.Left += dx; _sel.Right += dx; _sel.Top += dy; _sel.Bottom += dy;
        Redraw();
    }

    Grip GripAt(Win32.POINT p)
    {
        int t = 9; // grab distance in pixels
        bool l = Math.Abs(p.X - _sel.Left) <= t, r = Math.Abs(p.X - _sel.Right) <= t, top = Math.Abs(p.Y - _sel.Top) <= t, b = Math.Abs(p.Y - _sel.Bottom) <= t;
        bool inX = p.X > _sel.Left - t && p.X < _sel.Right + t, inY = p.Y > _sel.Top - t && p.Y < _sel.Bottom + t;
        if (top && l) return Grip.NW;
        if (top && r) return Grip.NE;
        if (b && l) return Grip.SW;
        if (b && r) return Grip.SE;
        if (top && inX) return Grip.N;
        if (b && inX) return Grip.S;
        if (l && inY) return Grip.W;
        if (r && inY) return Grip.E;
        return Contains(_sel, p) ? Grip.Move : Grip.None;
    }

    internal Cursor CursorAt(Win32.POINT p)
    {
        if (_phase != Phase.Adjusting) return Cursors.Cross;
        return GripAt(p) switch
        {
            Grip.Move => Cursors.SizeAll,
            Grip.N or Grip.S => Cursors.SizeNS,
            Grip.E or Grip.W => Cursors.SizeWE,
            Grip.NE or Grip.SW => Cursors.SizeNESW,
            Grip.NW or Grip.SE => Cursors.SizeNWSE,
            _ => Cursors.Cross,
        };
    }

    internal void Finish(bool ok)
    {
        if (_finished) return;
        _finished = true;
        foreach (var w in _windows) w.Close();
        var r = _sel;
        _done.TrySetResult(ok && r.Width >= 16 && r.Height >= 16 ? r : null);
    }

    // ── one veil per monitor ────────────────────────────────────────────────

    sealed class SelectorWindow : Window
    {
        readonly RegionSelector _s;
        public Win32.RECT Bounds { get; }
        readonly Veil _veil;
        readonly Border _toolbar, _hint;
        readonly TextBlock _hintText;
        double _scale = 1;
        DateTime _lastDown;

        public SelectorWindow(RegionSelector s, Win32.RECT bounds)
        {
            _s = s;
            Bounds = bounds;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Left = -32000; Top = -32000; Width = 10; Height = 10;
            FontFamily = (FontFamily)FindResource("Plex");
            var root = new Grid();
            _veil = new Veil(this);
            root.Children.Add(_veil);

            // the toolbar under the selection
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            var rec = new Button { Style = (Style)FindResource("BtnAccent"), Padding = new Thickness(12, 6, 12, 6) };
            var rp = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Tally");
            rp.Children.Add(dot);
            rp.Children.Add(new TextBlock { Text = Loc.T("sel.record") });
            rp.Children.Add(new TextBlock { Text = "  Enter", Opacity = 0.6, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            rec.Content = rp;
            rec.Click += (_, _) => _s.Finish(true);
            var cancel = new Button { Style = (Style)FindResource("IconBtn"), Margin = new Thickness(6, 0, 0, 0), Content = new Ico { Data = (Geometry)FindResource("IcoClose"), Width = 15, Height = 15 }, ToolTip = "Esc" };
            cancel.Click += (_, _) => _s.Finish(false);
            bar.Children.Add(rec);
            bar.Children.Add(cancel);
            _toolbar = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(6), Child = bar, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
            _toolbar.SetResourceReference(Border.BackgroundProperty, "Raised");
            _toolbar.SetResourceReference(Border.BorderBrushProperty, "Line");
            _toolbar.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35 };
            root.Children.Add(_toolbar);

            _hintText = new TextBlock { Text = Loc.T("sel.hint"), FontSize = 13 };
            _hintText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            _hint = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 8, 14, 8), Child = _hintText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 0, 0), BorderThickness = new Thickness(1), IsHitTestVisible = false };
            _hint.SetResourceReference(Border.BackgroundProperty, "Raised");
            _hint.SetResourceReference(Border.BorderBrushProperty, "Line");
            root.Children.Add(_hint);
            Content = root;

            SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                Win32.SetWindowDisplayAffinity(h, Win32.WDA_EXCLUDEFROMCAPTURE);
                Win32.SetWindowPos(h, Win32.HWND_TOPMOST, Bounds.Left, Bounds.Top, Bounds.Width, Bounds.Height, Win32.SWP_SHOWWINDOW);
            };
            DpiChanged += (_, _) => { _scale = VisualTreeHelper.GetDpi(this).DpiScaleX; Paint(); };
            Loaded += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                Win32.SetWindowPos(h, Win32.HWND_TOPMOST, Bounds.Left, Bounds.Top, Bounds.Width, Bounds.Height, Win32.SWP_SHOWWINDOW);
                _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                Opacity = 0;
                Springs.To(this, OpacityProperty, 1, 420, 1);
                Paint();
            };
            MouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInside(d, _toolbar)) return;
                bool dbl = (DateTime.Now - _lastDown).TotalMilliseconds < 350;
                _lastDown = DateTime.Now;
                CaptureMouse();
                _s.Down(Desktop(e.GetPosition(this)), dbl);
            };
            MouseMove += (_, e) =>
            {
                var p = Desktop(e.GetPosition(this));
                _s.Move(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                Cursor = _s.CursorAt(p);
            };
            MouseLeftButtonUp += (_, e) =>
            {
                if (!IsMouseCaptured) return;
                ReleaseMouseCapture();
                _s.Up(Desktop(e.GetPosition(this)));
            };
            MouseRightButtonUp += (_, _) => _s.Finish(false);
            PreviewKeyDown += (_, e) => { _s.Key(e.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; };
        }

        static bool IsInside(DependencyObject d, DependencyObject container)
        {
            for (var x = d; x != null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
                if (ReferenceEquals(x, container)) return true;
            return false;
        }

        Win32.POINT Desktop(Point p) => new() { X = Bounds.Left + (int)Math.Round(p.X * _scale), Y = Bounds.Top + (int)Math.Round(p.Y * _scale) };
        Size? _demo;

        public FrameworkElement Detach(double w, double h)
        {
            _demo = new Size(w, h);
            var root = (FrameworkElement)Content;
            Content = null;
            root.Width = w; root.Height = h;
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            Paint();
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            return root;
        }

        double W => _demo?.Width ?? ActualWidth;
        double H => _demo?.Height ?? ActualHeight;

        public Point Local(int x, int y) => new((x - Bounds.Left) / _scale, (y - Bounds.Top) / _scale);
        public Rect Local(Win32.RECT r) => new(Local(r.Left, r.Top), Local(r.Right, r.Bottom));

        public void Paint()
        {
            _veil.InvalidateVisual();
            bool mine = _s._phase == Phase.Adjusting && Contains(Bounds, new Win32.POINT { X = (_s._sel.Left + _s._sel.Right) / 2, Y = (_s._sel.Top + _s._sel.Bottom) / 2 });
            _toolbar.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
            if (mine)
            {
                var r = Local(_s._sel);
                _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double tw = _toolbar.DesiredSize.Width, th = _toolbar.DesiredSize.Height;
                double x = Math.Clamp(r.Left + (r.Width - tw) / 2, 8, Math.Max(8, W - tw - 8));
                double y = r.Bottom + 12 + th < H - 8 ? r.Bottom + 12 : r.Top - th - 12 > 8 ? r.Top - th - 12 : r.Bottom - th - 12;
                _toolbar.Margin = new Thickness(x, y, 0, 0);
            }
            bool cursorHere = Contains(Bounds, _s._cursor);
            _hint.Visibility = cursorHere && _s._phase == Phase.Hover ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The veil, the selection with its viewfinder corners and handles, the crosshair and the labels.</summary>
        sealed class Veil(SelectorWindow w) : FrameworkElement
        {
            protected override void OnRender(DrawingContext dc)
            {
                var s = w._s;
                var full = new Rect(0, 0, ActualWidth, ActualHeight);
                var veil = (Brush)FindResource("Veil");
                var accent = (Brush)FindResource("Accent");
                Rect? hole = null;
                if (s._phase is Phase.Drawing or Phase.Adjusting && s._sel.Width > 0) hole = Rect.Intersect(full, w.Local(s._sel));
                else if (s._hover >= 0) hole = Rect.Intersect(full, w.Local(s._targets[s._hover].Rect));
                if (hole is { IsEmpty: false } h)
                {
                    var g = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(h));
                    dc.DrawGeometry(veil, null, g);
                }
                else dc.DrawRectangle(veil, null, full);

                var face = new Typeface((FontFamily)FindResource("PlexMedium"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

                void Label(string text, Point at, bool above)
                {
                    var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 12.5, Brushes.White, dpi);
                    var box = new Rect(at.X, above ? at.Y - ft.Height - 12 : at.Y + 8, ft.Width + 16, ft.Height + 6);
                    box.X = Math.Clamp(box.X, 4, Math.Max(4, ActualWidth - box.Width - 4));
                    if (box.Y < 4) box.Y = at.Y + 8;
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xD8, 10, 12, 15)), null, box, 5, 5);
                    dc.DrawText(ft, new Point(box.X + 8, box.Y + 3));
                }

                if (s._phase == Phase.Hover && s._hover >= 0 && hole is { IsEmpty: false } hw)
                {
                    dc.DrawRectangle(null, new Pen(accent, 2), hw);
                    var t = s._targets[s._hover];
                    Label($"{Trim(t.Title)} · {t.Rect.Width} × {t.Rect.Height}", hw.TopLeft, true);
                }
                if (s._phase is Phase.Drawing or Phase.Adjusting && hole is { IsEmpty: false } r)
                {
                    dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255)), 1), r);
                    // viewfinder corners
                    double k = Math.Min(22, Math.Min(r.Width, r.Height) / 3);
                    var corner = new Pen(accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                    void C(Point a, Point m, Point z) { var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(a, false, false); c.LineTo(m, true, true); c.LineTo(z, true, true); } dc.DrawGeometry(null, corner, g); }
                    C(new(r.Left, r.Top + k), r.TopLeft, new(r.Left + k, r.Top));
                    C(new(r.Right - k, r.Top), r.TopRight, new(r.Right, r.Top + k));
                    C(new(r.Right, r.Bottom - k), r.BottomRight, new(r.Right - k, r.Bottom));
                    C(new(r.Left + k, r.Bottom), r.BottomLeft, new(r.Left, r.Bottom - k));
                    if (s._phase == Phase.Adjusting)
                    {
                        // handles on the edges
                        var fill = Brushes.White;
                        foreach (var p in new[] { new Point(r.Left + r.Width / 2, r.Top), new Point(r.Left + r.Width / 2, r.Bottom), new Point(r.Left, r.Top + r.Height / 2), new Point(r.Right, r.Top + r.Height / 2) })
                            dc.DrawRoundedRectangle(fill, new Pen(accent, 1.5), new Rect(p.X - 5, p.Y - 5, 10, 10), 2.5, 2.5);
                        // rule of thirds, faint
                        var third = new Pen(new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)), 1);
                        for (int i = 1; i < 3; i++)
                        {
                            dc.DrawLine(third, new(r.Left + r.Width * i / 3, r.Top), new(r.Left + r.Width * i / 3, r.Bottom));
                            dc.DrawLine(third, new(r.Left, r.Top + r.Height * i / 3), new(r.Right, r.Top + r.Height * i / 3));
                        }
                    }
                    Label($"{s._sel.Width} × {s._sel.Height}", r.TopLeft, true);
                }
                // crosshair and position while choosing
                if (s._phase == Phase.Hover && Contains(w.Bounds, s._cursor))
                {
                    var c = w.Local(s._cursor.X, s._cursor.Y);
                    var dash = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255)), 1) { DashStyle = new DashStyle([4, 4], 0) };
                    dc.DrawLine(dash, new Point(c.X, 0), new Point(c.X, ActualHeight));
                    dc.DrawLine(dash, new Point(0, c.Y), new Point(ActualWidth, c.Y));
                    Label($"{s._cursor.X}, {s._cursor.Y}", new Point(c.X + 14, c.Y + 10), false);
                }
            }

            static string Trim(string t) => t.Length > 40 ? t[..39] + "…" : t;
        }
    }
}
