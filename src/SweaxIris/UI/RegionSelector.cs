using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SweaxIris.Core;
using SweaxIris.Native;

namespace SweaxIris.UI;

/// <summary>
/// The region picker. A dim veil over every monitor and a dock at the top: the frame's shape (free, 16:9, 9:16,
/// 1:1, 4:3), its exact size in pixels (type it), ready-made sizes, full screen, Record and Cancel. Hover a window
/// and the frame glides onto it; click to take it, or drag to draw. Edges snap to windows and screen edges (hold
/// Alt to draw freely), a loupe shows the pixels under the pointer, the arrows nudge (Shift ×10, Ctrl resizes).
/// Enter or a double-click records, Esc cancels. All in physical pixels, so it is exact on mixed-DPI setups.
/// </summary>
public sealed class RegionSelector
{
    enum Phase { Hover, Drawing, Adjusting }
    enum Grip { None, Move, N, S, E, W, NE, NW, SE, SW }

    static readonly (string Label, double Ratio)[] Aspects = [("free", 0), ("16:9", 16 / 9.0), ("9:16", 9 / 16.0), ("1:1", 1), ("4:3", 4 / 3.0)];
    static readonly (int W, int H, string Note)[] Sizes =
        [(3840, 2160, "4K"), (2560, 1440, "1440p"), (1920, 1080, "1080p"), (1280, 720, "720p"), (1080, 1920, "9:16"), (1080, 1350, "4:5"), (1080, 1080, "1:1")];
    const int MinSize = 32, SnapDistance = 10, LoupeRadius = 7;

    /// <summary>The frame shape stays as it was left, for the next region in this run.</summary>
    static double _aspect;

    readonly List<SelectorWindow> _windows = [];
    readonly List<(Win32.RECT Rect, string Title)> _targets = [];
    readonly List<int> _edgesX = [], _edgesY = [];
    readonly TaskCompletionSource<Win32.RECT?> _done = new();
    Phase _phase = Phase.Hover;
    Grip _grip;
    Win32.POINT _cursor, _anchor;
    Win32.RECT _sel, _start, _bounds;
    int _hover = -1;
    int? _guideX, _guideY;
    bool _finished, _free;
    Rect _glide = Rect.Empty;
    bool _gliding;
    Func<Win32.POINT, BitmapSource?> _sample = Sample;
    BitmapSource? _loupe;
    bool _demo;

    /// <summary>For the README pictures: the picker's look for a given moment, on a made-up screen.</summary>
    public static FrameworkElement DemoVisual(double w, double h, Rect sel, string phase, Point cursor, Rect? hover, BitmapSource? backdrop = null, double aspect = 0)
    {
        var s = new RegionSelector
        {
            _phase = phase switch { "draw" => Phase.Drawing, "adjust" => Phase.Adjusting, _ => Phase.Hover },
            _sel = R(sel), _cursor = new Win32.POINT { X = (int)cursor.X, Y = (int)cursor.Y },
            _bounds = new Win32.RECT { Right = (int)w, Bottom = (int)h },
            _demo = true,
        };
        _aspect = aspect;
        if (hover is { } hr) { s._targets.Add((R(hr), "Atlas — Live dashboard")); s._hover = 0; s._glide = hr; }
        if (backdrop != null)
            s._sample = p =>
            {
                var r = new Int32Rect(Math.Clamp(p.X - LoupeRadius, 0, backdrop.PixelWidth - 15), Math.Clamp(p.Y - LoupeRadius, 0, backdrop.PixelHeight - 15), 15, 15);
                var c = new CroppedBitmap(backdrop, r);
                c.Freeze();
                return c;
            };
        if (s._phase != Phase.Adjusting) s._loupe = s._sample(s._cursor);
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
        // windows on screen, top to bottom, for click-to-take; their edges are what the frame snaps to
        foreach (var h in Win32.RealWindows())
        {
            var r = Win32.FrameOf(h);
            if (r.Width < 40 || r.Height < 40) continue;
            _targets.Add((r, Win32.GetWindowTitle(h)));
        }
        Win32.GetCursorPos(out _cursor);
        foreach (var (_, r, _) in Win32.MonitorList())
        {
            var w = new SelectorWindow(this, r);
            _windows.Add(w);
            w.Show();
        }
        foreach (var r in _targets.Select(t => t.Rect).Concat(_windows.Select(w => w.Bounds)))
        {
            _edgesX.Add(r.Left); _edgesX.Add(r.Right);
            _edgesY.Add(r.Top); _edgesY.Add(r.Bottom);
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
        _loupe = _phase == Phase.Hover ? _sample(_cursor) : null;
        Redraw();
    }

    static BitmapSource? Sample(Win32.POINT p)
    {
        try { return Win32.CaptureScreen(new Win32.RECT { Left = p.X - LoupeRadius, Top = p.Y - LoupeRadius, Right = p.X + LoupeRadius + 1, Bottom = p.Y + LoupeRadius + 1 }); }
        catch { return null; }
    }

    static bool Contains(Win32.RECT r, Win32.POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    static Win32.RECT Clamp(Win32.RECT r, Win32.RECT b) => new()
    {
        Left = Math.Max(r.Left, b.Left), Top = Math.Max(r.Top, b.Top), Right = Math.Min(r.Right, b.Right), Bottom = Math.Min(r.Bottom, b.Bottom),
    };

    /// <summary>Moves a rectangle (keeping its size) until it lies inside the bounds.</summary>
    static Win32.RECT Inside(Win32.RECT r, Win32.RECT b)
    {
        int w = Math.Min(r.Width, b.Width), h = Math.Min(r.Height, b.Height);
        int x = Math.Clamp(r.Left, b.Left, b.Right - w), y = Math.Clamp(r.Top, b.Top, b.Bottom - h);
        return new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    void UpdateHover()
    {
        int was = _hover;
        _hover = -1;
        if (_phase == Phase.Hover)
            for (int i = 0; i < _targets.Count; i++)
                if (Contains(_targets[i].Rect, _cursor)) { _hover = i; break; }
        if (_hover != was) Glide();
    }

    /// <summary>The hover frame glides from window to window instead of jumping.</summary>
    void Glide()
    {
        if (_hover < 0) { _glide = Rect.Empty; return; }
        var t = _targets[_hover].Rect;
        var target = new Rect(t.Left, t.Top, t.Width, t.Height);
        if (_glide.IsEmpty || _demo) { _glide = target; return; }
        if (_gliding) return;
        _gliding = true;
        Springs.Run(dt =>
        {
            if (_finished || _hover < 0) { _gliding = false; return false; }
            var g = _targets[_hover].Rect;
            double k = 1 - Math.Exp(-dt * 20);
            double x = _glide.X + (g.Left - _glide.X) * k, y = _glide.Y + (g.Top - _glide.Y) * k;
            double w = _glide.Width + (g.Width - _glide.Width) * k, h = _glide.Height + (g.Height - _glide.Height) * k;
            bool done = Math.Abs(g.Left - x) + Math.Abs(g.Top - y) + Math.Abs(g.Width - w) + Math.Abs(g.Height - h) < 1;
            _glide = done ? new Rect(g.Left, g.Top, g.Width, g.Height) : new Rect(x, y, Math.Max(1, w), Math.Max(1, h));
            Redraw();
            if (done) _gliding = false;
            return !done;
        });
    }

    internal void Redraw() { foreach (var w in _windows) w.Paint(); }

    // ── snapping ────────────────────────────────────────────────────────────

    int SnapX(int x) => Snap(x, _edgesX, ref _guideX);
    int SnapY(int y) => Snap(y, _edgesY, ref _guideY);

    int Snap(int v, List<int> edges, ref int? guide)
    {
        if (_free) return v;
        int best = v, d = SnapDistance + 1;
        foreach (var e in edges)
        {
            int dd = Math.Abs(e - v);
            if (dd < d) { d = dd; best = e; }
        }
        if (d > SnapDistance) return v;
        guide = best;
        return best;
    }

    double Ratio(bool shift) => _aspect > 0 ? _aspect : shift ? 16 / 9.0 : 0;

    // ── input (in desktop pixels) ───────────────────────────────────────────

    internal void Down(Win32.POINT p, bool doubleClick)
    {
        _cursor = p;
        if (_phase == Phase.Adjusting)
        {
            if (doubleClick && Contains(_sel, p)) { Finish(true); return; }
            _grip = GripAt(p);
            if (_grip != Grip.None) { _anchor = p; _start = _sel; Redraw(); return; }
        }
        _phase = Phase.Drawing;
        _bounds = Win32.MonitorInfo(Win32.MonitorFromPoint(p, Win32.MONITOR_DEFAULTTONEAREST)).rcMonitor;
        _guideX = _guideY = null;
        _anchor = new Win32.POINT { X = SnapX(p.X), Y = SnapY(p.Y) };
        _sel = new Win32.RECT { Left = _anchor.X, Top = _anchor.Y, Right = _anchor.X, Bottom = _anchor.Y };
        Redraw();
    }

    internal void Move(Win32.POINT p, bool shift, bool alt)
    {
        _cursor = p;
        _free = alt;
        _guideX = _guideY = null;
        if (_phase == Phase.Drawing) Draw(p, shift);
        else if (_phase == Phase.Adjusting && _grip != Grip.None && Mouse.LeftButton == MouseButtonState.Pressed) Adjust(p, shift);
        else UpdateHover();
        bool showLoupe = _phase is Phase.Hover or Phase.Drawing || (_grip is not Grip.None and not Grip.Move && Mouse.LeftButton == MouseButtonState.Pressed);
        _loupe = showLoupe ? _sample(p) : null;
        Redraw();
    }

    void Draw(Win32.POINT p, bool shift)
    {
        int x0 = Math.Clamp(_anchor.X, _bounds.Left, _bounds.Right), y0 = Math.Clamp(_anchor.Y, _bounds.Top, _bounds.Bottom);
        int x1 = Math.Clamp(SnapX(p.X), _bounds.Left, _bounds.Right), y1 = Math.Clamp(SnapY(p.Y), _bounds.Top, _bounds.Bottom);
        double ratio = Ratio(shift);
        if (ratio > 0)
        {
            // the longer pull leads; the frame never leaves the monitor
            int sx = x1 >= x0 ? 1 : -1, sy = y1 >= y0 ? 1 : -1;
            double w = Math.Abs(x1 - x0), h = Math.Abs(y1 - y0);
            if (w > h * ratio) h = w / ratio; else w = h * ratio;
            double maxW = sx > 0 ? _bounds.Right - x0 : x0 - _bounds.Left, maxH = sy > 0 ? _bounds.Bottom - y0 : y0 - _bounds.Top;
            if (w > maxW) { w = maxW; h = w / ratio; }
            if (h > maxH) { h = maxH; w = h * ratio; }
            x1 = x0 + sx * (int)Math.Round(w);
            y1 = y0 + sy * (int)Math.Round(h);
            if (Math.Abs(w - Math.Abs(p.X - x0)) > 1) _guideX = null;
            if (Math.Abs(h - Math.Abs(p.Y - y0)) > 1) _guideY = null;
        }
        _sel = new Win32.RECT { Left = Math.Min(x0, x1), Top = Math.Min(y0, y1), Right = Math.Max(x0, x1), Bottom = Math.Max(y0, y1) };
    }

    void Adjust(Win32.POINT p, bool shift)
    {
        int dx = p.X - _anchor.X, dy = p.Y - _anchor.Y;
        var r = _start;
        if (_grip == Grip.Move)
        {
            dx = Math.Clamp(dx, _bounds.Left - r.Left, _bounds.Right - r.Right);
            dy = Math.Clamp(dy, _bounds.Top - r.Top, _bounds.Bottom - r.Bottom);
            r.Left += dx; r.Right += dx; r.Top += dy; r.Bottom += dy;
            // either edge may snap; the nearer one wins
            int? gl = null, gr = null, gt = null, gb = null;
            int sl = Snap(r.Left, _edgesX, ref gl) - r.Left, sr = Snap(r.Right, _edgesX, ref gr) - r.Right;
            int st = Snap(r.Top, _edgesY, ref gt) - r.Top, sb = Snap(r.Bottom, _edgesY, ref gb) - r.Bottom;
            int mx = gl != null && (gr == null || Math.Abs(sl) <= Math.Abs(sr)) ? sl : gr != null ? sr : 0;
            int my = gt != null && (gb == null || Math.Abs(st) <= Math.Abs(sb)) ? st : gb != null ? sb : 0;
            _guideX = mx == sl && gl != null ? gl : gr;
            _guideY = my == st && gt != null ? gt : gb;
            r.Left += mx; r.Right += mx; r.Top += my; r.Bottom += my;
            _sel = Inside(r, _bounds);
            return;
        }
        if (_grip is Grip.W or Grip.NW or Grip.SW) r.Left = Math.Min(r.Right - MinSize, SnapX(r.Left + dx));
        if (_grip is Grip.E or Grip.NE or Grip.SE) r.Right = Math.Max(r.Left + MinSize, SnapX(r.Right + dx));
        if (_grip is Grip.N or Grip.NW or Grip.NE) r.Top = Math.Min(r.Bottom - MinSize, SnapY(r.Top + dy));
        if (_grip is Grip.S or Grip.SW or Grip.SE) r.Bottom = Math.Max(r.Top + MinSize, SnapY(r.Bottom + dy));
        double ratio = Ratio(shift);
        bool left = _grip is Grip.W or Grip.NW or Grip.SW, top = _grip is Grip.N or Grip.NW or Grip.NE;
        if (ratio > 0)
        {
            int w = r.Width, h = r.Height;
            if (_grip is Grip.E or Grip.W) { h = (int)Math.Round(w / ratio); int cy = (_start.Top + _start.Bottom) / 2; r.Top = cy - h / 2; r.Bottom = r.Top + h; _guideY = null; }
            else if (_grip is Grip.N or Grip.S) { w = (int)Math.Round(h * ratio); int cx = (_start.Left + _start.Right) / 2; r.Left = cx - w / 2; r.Right = r.Left + w; _guideX = null; }
            else
            {
                if (w > h * ratio) { h = (int)Math.Round(w / ratio); _guideY = null; } else { w = (int)Math.Round(h * ratio); _guideX = null; }
                if (left) r.Left = r.Right - w; else r.Right = r.Left + w;
                if (top) r.Top = r.Bottom - h; else r.Bottom = r.Top + h;
            }
        }
        r = Clamp(r, _bounds);
        if (ratio > 0)
        {
            // clamping may have cut one side: shrink the other to keep the shape
            int w = r.Width, h = r.Height;
            if (w > h * ratio + 1) w = (int)Math.Round(h * ratio); else if (h > w / ratio + 1) h = (int)Math.Round(w / ratio);
            if (left) r.Left = r.Right - w; else r.Right = r.Left + w;
            if (top) r.Top = r.Bottom - h; else r.Bottom = r.Top + h;
        }
        _sel = r;
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
            if (_sel.Width < MinSize) _sel.Right = Math.Min(_bounds.Right, _sel.Left + MinSize);
            if (_sel.Height < MinSize) _sel.Bottom = Math.Min(_bounds.Bottom, _sel.Top + MinSize);
            _phase = Phase.Adjusting;
            _hover = -1;
            _glide = Rect.Empty;
        }
        _grip = Grip.None;
        _guideX = _guideY = null;
        _loupe = null;
        Redraw();
    }

    internal void Key(Key key, bool shift, bool ctrl)
    {
        if (key == System.Windows.Input.Key.Escape) { Finish(false); return; }
        if (key == System.Windows.Input.Key.F) { FullScreen(null); return; }
        if (_phase != Phase.Adjusting) return;
        if (key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { Finish(true); return; }
        int step = shift ? 10 : 1, dx = 0, dy = 0;
        if (key == System.Windows.Input.Key.Left) dx = -step;
        if (key == System.Windows.Input.Key.Right) dx = step;
        if (key == System.Windows.Input.Key.Up) dy = -step;
        if (key == System.Windows.Input.Key.Down) dy = step;
        if (dx == 0 && dy == 0) return;
        if (ctrl)
        {
            // Ctrl + arrows: the right and bottom edges move
            var r = _sel;
            r.Right = Math.Clamp(r.Right + dx, r.Left + MinSize, _bounds.Right);
            r.Bottom = Math.Clamp(r.Bottom + dy, r.Top + MinSize, _bounds.Bottom);
            if (_aspect > 0) { if (dx != 0) r.Bottom = Math.Min(_bounds.Bottom, r.Top + (int)Math.Round(r.Width / _aspect)); else r.Right = Math.Min(_bounds.Right, r.Left + (int)Math.Round(r.Height * _aspect)); }
            _sel = r;
        }
        else
        {
            dx = Math.Clamp(dx, _bounds.Left - _sel.Left, _bounds.Right - _sel.Right);
            dy = Math.Clamp(dy, _bounds.Top - _sel.Top, _bounds.Bottom - _sel.Bottom);
            _sel.Left += dx; _sel.Right += dx; _sel.Top += dy; _sel.Bottom += dy;
        }
        Redraw();
    }

    // ── the dock's actions ──────────────────────────────────────────────────

    internal void SetAspect(double ratio)
    {
        _aspect = ratio;
        if (_phase == Phase.Adjusting && ratio > 0)
        {
            // the largest frame of that shape inside the current one, around its centre
            int w = _sel.Width, h = _sel.Height;
            if (w > h * ratio) w = (int)Math.Round(h * ratio); else h = (int)Math.Round(w / ratio);
            int cx = (_sel.Left + _sel.Right) / 2, cy = (_sel.Top + _sel.Bottom) / 2;
            _sel = Inside(new Win32.RECT { Left = cx - w / 2, Top = cy - h / 2, Right = cx - w / 2 + w, Bottom = cy - h / 2 + h }, _bounds);
        }
        Redraw();
    }

    /// <summary>An exact size, centred where the frame is (or on the monitor).</summary>
    internal void SetSize(int w, int h, Win32.RECT monitor)
    {
        if (_phase != Phase.Adjusting) { _bounds = monitor; _sel = monitor; }
        w = Math.Clamp(w, MinSize, _bounds.Width);
        h = Math.Clamp(h, MinSize, _bounds.Height);
        int cx = (_sel.Left + _sel.Right) / 2, cy = (_sel.Top + _sel.Bottom) / 2;
        _sel = Inside(new Win32.RECT { Left = cx - w / 2, Top = cy - h / 2, Right = cx - w / 2 + w, Bottom = cy - h / 2 + h }, _bounds);
        if (_aspect > 0 && Math.Abs(w / (double)h - _aspect) > 0.01) _aspect = 0;
        _phase = Phase.Adjusting;
        _hover = -1;
        _glide = Rect.Empty;
        Redraw();
    }

    internal void FullScreen(Win32.RECT? monitor)
    {
        _bounds = monitor ?? Win32.MonitorInfo(Win32.MonitorFromPoint(_cursor, Win32.MONITOR_DEFAULTTONEAREST)).rcMonitor;
        _sel = _bounds;
        _aspect = 0;
        _phase = Phase.Adjusting;
        _hover = -1;
        _glide = Rect.Empty;
        _loupe = null;
        Redraw();
    }

    Grip GripAt(Win32.POINT p)
    {
        int t = 10; // grab distance in pixels
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
        if (ok && _phase != Phase.Adjusting) return;
        _finished = true;
        foreach (var w in _windows) w.Close();
        var r = _sel;
        _done.TrySetResult(ok && r.Width >= 16 && r.Height >= 16 ? r : null);
    }

    static string AspectName(int w, int h)
    {
        if (h <= 0) return "";
        double a = w / (double)h;
        foreach (var (label, ratio) in Aspects.Skip(1).Append(("21:9", 64 / 27.0)).Append(("4:5", 0.8)))
            if (Math.Abs(a - ratio) < 0.006) return label;
        return "";
    }

    // ── one veil per monitor ────────────────────────────────────────────────

    sealed class SelectorWindow : Window
    {
        readonly RegionSelector _s;
        public Win32.RECT Bounds { get; }
        readonly Veil _veil;
        readonly Border _dock;
        readonly Segments _shape = new() { Width = 300 };
        readonly TextBox _w, _h;
        readonly Button _record;
        readonly TextBlock _hint;
        readonly TranslateTransform _dockMove = new(0, 0);
        double _scale = 1;
        DateTime _lastDown;
        bool _updating;

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
            RenderOptions.SetBitmapScalingMode(_veil, BitmapScalingMode.NearestNeighbor);
            root.Children.Add(_veil);

            // the dock: shape, exact size, ready-made sizes, full screen, record, cancel
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            _shape.SetItems(Aspects.Select(a => ((object)a.Ratio, a.Ratio == 0 ? Loc.T("sel.free") : a.Label)));
            _shape.Changed += v => { if (!_updating) _s.SetAspect((double)v); };
            _shape.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_shape);
            row.Children.Add(Rule());

            _w = SizeBox();
            _h = SizeBox();
            row.Children.Add(Caption("W"));
            row.Children.Add(_w);
            row.Children.Add(new TextBlock { Text = "×", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("TextFaint") });
            row.Children.Add(Caption("H"));
            row.Children.Add(_h);
            var sizes = DockButton("IcoChevron", Loc.T("sel.sizes"), null);
            sizes.Click += (_, _) => SizesMenu(sizes);
            sizes.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(sizes);
            row.Children.Add(Rule());

            var full = DockButton("IcoScreen", Loc.T("sel.full"), "F");
            full.Click += (_, _) => _s.FullScreen(Bounds);
            row.Children.Add(full);
            row.Children.Add(Rule());

            _record = new Button { Style = (Style)FindResource("BtnAccent"), Padding = new Thickness(14, 7, 14, 7) };
            var rp = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Tally");
            rp.Children.Add(dot);
            rp.Children.Add(new TextBlock { Text = Loc.T("sel.record"), FontWeight = FontWeights.SemiBold });
            rp.Children.Add(new TextBlock { Text = "  Enter", Opacity = 0.6, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            _record.Content = rp;
            _record.Click += (_, _) => _s.Finish(true);
            row.Children.Add(_record);
            var cancel = new Button { Style = (Style)FindResource("IconBtn"), Margin = new Thickness(6, 0, 0, 0), Width = 34, Height = 34, Content = new Ico { Data = (Geometry)FindResource("IcoClose"), Width = 15, Height = 15 }, ToolTip = Loc.T("sel.cancel") };
            cancel.Click += (_, _) => _s.Finish(false);
            row.Children.Add(cancel);

            _hint = new TextBlock { FontSize = 12, Margin = new Thickness(4, 9, 4, 1), TextAlignment = TextAlignment.Center };
            _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            var dockBody = new StackPanel();
            dockBody.Children.Add(row);
            dockBody.Children.Add(_hint);
            _dock = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 10, 10, 8), Child = dockBody,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 22, 0, 0),
                BorderThickness = new Thickness(1), RenderTransform = _dockMove, Visibility = Visibility.Collapsed,
            };
            _dock.SetResourceReference(Border.BackgroundProperty, "Raised");
            _dock.SetResourceReference(Border.BorderBrushProperty, "Line");
            _dock.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.45, Direction = 270 };
            root.Children.Add(_dock);
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
                // the dock drops in from above
                _dockMove.Y = -26;
                Springs.To(_dockMove, TranslateTransform.YProperty, 0, 380, 0.62);
                Paint();
            };
            MouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInside(d, _dock)) return;
                Keyboard.Focus(this);
                bool dbl = (DateTime.Now - _lastDown).TotalMilliseconds < 350;
                _lastDown = DateTime.Now;
                CaptureMouse();
                _s.Down(Desktop(e.GetPosition(this)), dbl);
            };
            MouseMove += (_, e) =>
            {
                if (!IsMouseCaptured && e.OriginalSource is DependencyObject d && IsInside(d, _dock)) { Cursor = Cursors.Arrow; return; }
                var p = Desktop(e.GetPosition(this));
                _s.Move(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                Cursor = _s.CursorAt(p);
            };
            MouseLeftButtonUp += (_, e) =>
            {
                if (!IsMouseCaptured) return;
                ReleaseMouseCapture();
                _s.Up(Desktop(e.GetPosition(this)));
            };
            MouseRightButtonUp += (_, _) => _s.Finish(false);
            PreviewKeyDown += (_, e) =>
            {
                if (Keyboard.FocusedElement is TextBox tb)
                {
                    // typing a size: Enter applies it, Esc still cancels the picker
                    if (e.Key == System.Windows.Input.Key.Escape) { _s.Finish(false); e.Handled = true; }
                    else if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Tab) { Commit(tb); if (e.Key == System.Windows.Input.Key.Enter) Keyboard.Focus(this); else (tb == _w ? _h : _w).Focus(); e.Handled = true; }
                    return;
                }
                var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                _s.Key(key, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
            };
        }

        static Border Rule()
        {
            var b = new Border { Width = 1, Margin = new Thickness(12, 6, 12, 6) };
            b.SetResourceReference(Border.BackgroundProperty, "Line");
            return b;
        }

        static TextBlock Caption(string t)
        {
            var c = new TextBlock { Text = t, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            c.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
            return c;
        }

        TextBox SizeBox()
        {
            var tb = new TextBox { Width = 64, Height = 32, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13, Padding = new Thickness(2, 0, 2, 0) };
            tb.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            tb.LostKeyboardFocus += (_, _) => Commit(tb);
            tb.GotKeyboardFocus += (_, _) => tb.SelectAll();
            tb.PreviewMouseLeftButtonDown += (_, e) => { if (!tb.IsKeyboardFocusWithin) { tb.Focus(); e.Handled = true; } };
            return tb;
        }

        Button DockButton(string icon, string text, string? key)
        {
            var b = new Button { Style = (Style)FindResource("BtnGhost"), Padding = new Thickness(10, 7, 12, 7) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon != "IcoChevron") sp.Children.Add(new Ico { Data = (Geometry)FindResource(icon), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            if (icon == "IcoChevron") sp.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L4,4 L8,0"), StrokeThickness = 1.5, Stroke = Theme.Brush("TextDim"), Margin = new Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            if (key != null) sp.Children.Add(new TextBlock { Text = "  " + key, Opacity = 0.5, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            b.Content = sp;
            return b;
        }

        void SizesMenu(Button anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var (w, h, note) in Sizes)
            {
                var mi = new MenuItem { Header = $"{w} × {h}   {note}", IsEnabled = w <= Bounds.Width && h <= Bounds.Height };
                mi.Click += (_, _) => _s.SetSize(w, h, Bounds);
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        }

        void Commit(TextBox tb)
        {
            if (_updating || !int.TryParse(tb.Text, out int v)) return;
            var sel = _s._phase == Phase.Adjusting ? _s._sel : Bounds;
            int w = tb == _w ? v : sel.Width, h = tb == _h ? v : sel.Height;
            if (_aspect > 0) { if (tb == _w) h = (int)Math.Round(w / _aspect); else w = (int)Math.Round(h * _aspect); }
            if (w == sel.Width && h == sel.Height && _s._phase == Phase.Adjusting) return;
            _s.SetSize(w, h, Bounds);
        }

        static bool IsInside(DependencyObject d, DependencyObject container)
        {
            for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
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

        public Point Local(double x, double y) => new((x - Bounds.Left) / _scale, (y - Bounds.Top) / _scale);
        public Rect Local(Win32.RECT r) => new(Local(r.Left, r.Top), Local(r.Right, r.Bottom));
        public Rect Local(Rect r) => new(Local(r.Left, r.Top), Local(r.Right, r.Bottom));

        public void Paint()
        {
            _veil.InvalidateVisual();
            // the dock lives on the monitor of the frame, or of the pointer while choosing
            var c = _s._phase == Phase.Adjusting ? new Win32.POINT { X = (_s._sel.Left + _s._sel.Right) / 2, Y = (_s._sel.Top + _s._sel.Bottom) / 2 } : _s._cursor;
            bool active = Contains(Bounds, c);
            _dock.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (!active) return;
            _updating = true;
            if (!Equals(_shape.Value, _aspect)) _shape.Value = _aspect;
            var sel = _s._phase == Phase.Drawing || _s._phase == Phase.Adjusting ? _s._sel : _s._hover >= 0 ? Clamp(_s._targets[_s._hover].Rect, Bounds) : Bounds;
            if (!_w.IsKeyboardFocusWithin) _w.Text = sel.Width.ToString();
            if (!_h.IsKeyboardFocusWithin) _h.Text = sel.Height.ToString();
            _updating = false;
            bool ready = _s._phase == Phase.Adjusting;
            _record.IsEnabled = ready;
            _record.Opacity = ready ? 1 : 0.45;
            _hint.Text = Loc.T(ready ? "sel.hint.adjust" : "sel.hint");
            // the dock steps aside when the pointer or the frame comes up under it
            var cur = Local(_s._cursor.X, _s._cursor.Y);
            bool under = cur.Y < 150 && _s._phase != Phase.Adjusting && Math.Abs(cur.X - W / 2) < 420;
            _dock.VerticalAlignment = under && _s._phase == Phase.Drawing ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            _dock.Margin = _dock.VerticalAlignment == VerticalAlignment.Top ? new Thickness(0, 22, 0, 0) : new Thickness(0, 0, 0, 22);
        }

        /// <summary>The veil, the frame with its viewfinder corners and handles, guides, the crosshair, labels and the loupe.</summary>
        sealed class Veil(SelectorWindow w) : FrameworkElement
        {
            protected override void OnRender(DrawingContext dc)
            {
                var s = w._s;
                var full = new Rect(0, 0, ActualWidth, ActualHeight);
                var veil = (Brush)FindResource("Veil");
                var accent = (Brush)FindResource("Accent");
                var accentColor = (Color)FindResource("AccentColor");
                Rect? hole = null;
                if (s._phase is Phase.Drawing or Phase.Adjusting && s._sel.Width > 0) hole = Rect.Intersect(full, w.Local(s._sel));
                else if (!s._glide.IsEmpty) hole = Rect.Intersect(full, w.Local(s._glide));
                if (hole is { IsEmpty: false } h) dc.DrawGeometry(veil, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(h)));
                else dc.DrawRectangle(veil, null, full);

                var face = new Typeface((FontFamily)FindResource("Plex"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
                var bold = new Typeface((FontFamily)FindResource("Plex"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var pill = new SolidColorBrush(Color.FromArgb(0xE6, 10, 12, 15));
                FormattedText Text(string t, Typeface f, double size, Brush b) => new(t, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, f, size, b, dpi);

                void Label(string text, string? dim, Point at, bool above)
                {
                    var ft = Text(text, bold, 12.5, Brushes.White);
                    var fd = dim != null ? Text(dim, face, 12, new SolidColorBrush(Color.FromArgb(0xA8, 255, 255, 255))) : null;
                    double width = ft.Width + (fd != null ? fd.Width + 10 : 0);
                    var box = new Rect(at.X, above ? at.Y - ft.Height - 14 : at.Y + 8, width + 18, ft.Height + 8);
                    box.X = Math.Clamp(box.X, 4, Math.Max(4, ActualWidth - box.Width - 4));
                    if (box.Y < 4) box.Y = at.Y + 8;
                    dc.DrawRoundedRectangle(pill, null, box, 6, 6);
                    dc.DrawText(ft, new Point(box.X + 9, box.Y + 4));
                    if (fd != null) dc.DrawText(fd, new Point(box.X + 9 + ft.Width + 10, box.Y + 4.5));
                }

                // a window under the pointer: the frame glides onto it
                if (s._phase == Phase.Hover && !s._glide.IsEmpty && hole is { IsEmpty: false } hw)
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x1C, accentColor.R, accentColor.G, accentColor.B)), new Pen(accent, 2), hw);
                    if (s._hover >= 0)
                    {
                        var t = s._targets[s._hover];
                        Label(Trim(t.Title), $"{t.Rect.Width} × {t.Rect.Height}", hw.TopLeft, true);
                    }
                }
                // snapping guides across the monitor
                var guide = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, accentColor.R, accentColor.G, accentColor.B)), 1) { DashStyle = new DashStyle([5, 4], 0) };
                if (s._guideX is int gx) { var x = w.Local(gx, 0).X; dc.DrawLine(guide, new Point(x, 0), new Point(x, ActualHeight)); }
                if (s._guideY is int gy) { var y = w.Local(0, gy).Y; dc.DrawLine(guide, new Point(0, y), new Point(ActualWidth, y)); }

                if (s._phase is Phase.Drawing or Phase.Adjusting && hole is { IsEmpty: false } r)
                {
                    dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 255, 255, 255)), 1), r);
                    // while the frame is being pulled: thirds, to place things
                    if (s._phase == Phase.Drawing || s._grip != Grip.None)
                    {
                        var third = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), 1);
                        for (int i = 1; i < 3; i++)
                        {
                            dc.DrawLine(third, new(r.Left + r.Width * i / 3, r.Top), new(r.Left + r.Width * i / 3, r.Bottom));
                            dc.DrawLine(third, new(r.Left, r.Top + r.Height * i / 3), new(r.Right, r.Top + r.Height * i / 3));
                        }
                    }
                    // viewfinder corners
                    double k = Math.Min(24, Math.Min(r.Width, r.Height) / 3);
                    var corner = new Pen(accent, 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                    void C(Point a, Point m, Point z) { var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(a, false, false); c.LineTo(m, true, true); c.LineTo(z, true, true); } dc.DrawGeometry(null, corner, g); }
                    C(new(r.Left, r.Top + k), r.TopLeft, new(r.Left + k, r.Top));
                    C(new(r.Right - k, r.Top), r.TopRight, new(r.Right, r.Top + k));
                    C(new(r.Right, r.Bottom - k), r.BottomRight, new(r.Right - k, r.Bottom));
                    C(new(r.Left + k, r.Bottom), r.BottomLeft, new(r.Left, r.Bottom - k));
                    if (s._phase == Phase.Adjusting)
                    {
                        // handles on the four edges, as round grips
                        foreach (var p in new[] { new Point(r.Left + r.Width / 2, r.Top), new Point(r.Left + r.Width / 2, r.Bottom), new Point(r.Left, r.Top + r.Height / 2), new Point(r.Right, r.Top + r.Height / 2) })
                            dc.DrawEllipse(Brushes.White, new Pen(accent, 2), p, 5.5, 5.5);
                    }
                    string shape = AspectName(s._sel.Width, s._sel.Height);
                    Label($"{s._sel.Width} × {s._sel.Height}", shape.Length > 0 ? shape : null, r.TopLeft, true);
                }
                // crosshair while choosing
                bool here = Contains(w.Bounds, s._cursor);
                if (s._phase == Phase.Hover && here)
                {
                    var c = w.Local(s._cursor.X, s._cursor.Y);
                    var dash = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)), 1) { DashStyle = new DashStyle([4, 4], 0) };
                    dc.DrawLine(dash, new Point(c.X, 0), new Point(c.X, ActualHeight));
                    dc.DrawLine(dash, new Point(0, c.Y), new Point(ActualWidth, c.Y));
                }
                if (here && s._loupe is { } lp) Loupe(dc, lp, w.Local(s._cursor.X, s._cursor.Y), accent, face, dpi);
            }

            /// <summary>The pixels under the pointer, eight times larger, with the exact position and colour.</summary>
            void Loupe(DrawingContext dc, BitmapSource px, Point at, Brush accent, Typeface face, double dpi)
            {
                const double Z = 8, N = LoupeRadius * 2 + 1, S = N * Z;
                double x = at.X + 26, y = at.Y + 26;
                if (x + S > ActualWidth - 8) x = at.X - 26 - S;
                if (y + S + 30 > ActualHeight - 8) y = at.Y - 26 - S - 30;
                var box = new Rect(x, y, S, S);
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), 6), box, 12, 12);
                dc.PushClip(new RectangleGeometry(box, 12, 12));
                dc.DrawImage(px, box);
                var cross = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
                dc.DrawRectangle(cross, null, new Rect(x, y + LoupeRadius * Z, S, Z));
                dc.DrawRectangle(cross, null, new Rect(x + LoupeRadius * Z, y, Z, S));
                dc.Pop();
                dc.DrawRectangle(null, new Pen(Brushes.White, 1.5), new Rect(x + LoupeRadius * Z, y + LoupeRadius * Z, Z, Z));
                dc.DrawRoundedRectangle(null, new Pen(accent, 2), box, 12, 12);
                // position and colour of the centre pixel
                string colour = "";
                try
                {
                    var conv = new FormatConvertedBitmap(px, PixelFormats.Bgra32, null, 0);
                    var one = new byte[4];
                    conv.CopyPixels(new Int32Rect(Math.Min(LoupeRadius, conv.PixelWidth - 1), Math.Min(LoupeRadius, conv.PixelHeight - 1), 1, 1), one, 4, 0);
                    colour = $"  #{one[2]:X2}{one[1]:X2}{one[0]:X2}";
                }
                catch { }
                var ft = new FormattedText($"{w._s._cursor.X}, {w._s._cursor.Y}{colour}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11.5, Brushes.White, dpi);
                var tag = new Rect(x, y + S + 6, Math.Max(S, ft.Width + 16), ft.Height + 8);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE6, 10, 12, 15)), null, tag, 6, 6);
                dc.DrawText(ft, new Point(tag.X + (tag.Width - ft.Width) / 2, tag.Y + 4));
            }

            static string Trim(string t) => t.Length > 44 ? t[..43] + "…" : t;
        }
    }
}
