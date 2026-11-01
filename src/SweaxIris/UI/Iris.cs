using System.Windows;
using System.Windows.Media;

namespace SweaxIris.UI;

/// <summary>
/// The camera iris SweaxIris is named after: blades around a polygon opening, a lens-barrel scale around them
/// and light coming through the middle. Every property springs, so the record button opens like a lens.
/// </summary>
public sealed class Iris : FrameworkElement
{
    public static readonly DependencyProperty ApertureProperty = Prop(nameof(Aperture), 0.2);
    public static readonly DependencyProperty TurnProperty = Prop(nameof(Turn), 0.0);
    public static readonly DependencyProperty GlowProperty = Prop(nameof(Glow), 0.0);
    public static readonly DependencyProperty ScaleTurnProperty = Prop(nameof(ScaleTurn), 0.0);
    public static readonly DependencyProperty CoreProperty = DependencyProperty.Register(nameof(Core), typeof(Color), typeof(Iris),
        new FrameworkPropertyMetadata(Color.FromRgb(0x2B, 0xD4, 0xB4), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowScaleProperty = DependencyProperty.Register(nameof(ShowScale), typeof(bool), typeof(Iris),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BladesProperty = DependencyProperty.Register(nameof(Blades), typeof(int), typeof(Iris),
        new FrameworkPropertyMetadata(7, FrameworkPropertyMetadataOptions.AffectsRender));

    static DependencyProperty Prop(string name, double def) => DependencyProperty.Register(name, typeof(double), typeof(Iris),
        new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 = shut, 1 = wide open.</summary>
    public double Aperture { get => (double)GetValue(ApertureProperty); set => SetValue(ApertureProperty, value); }
    /// <summary>Extra rotation of the blades, degrees.</summary>
    public double Turn { get => (double)GetValue(TurnProperty); set => SetValue(TurnProperty, value); }
    /// <summary>Light around the rim (recording), 0..1.</summary>
    public double Glow { get => (double)GetValue(GlowProperty); set => SetValue(GlowProperty, value); }
    /// <summary>Rotation of the engraved scale, degrees.</summary>
    public double ScaleTurn { get => (double)GetValue(ScaleTurnProperty); set => SetValue(ScaleTurnProperty, value); }
    /// <summary>The colour seen through the opening.</summary>
    public Color Core { get => (Color)GetValue(CoreProperty); set => SetValue(CoreProperty, value); }
    public bool ShowScale { get => (bool)GetValue(ShowScaleProperty); set => SetValue(ShowScaleProperty, value); }
    public int Blades { get => (int)GetValue(BladesProperty); set => SetValue(BladesProperty, value); }

    /// <summary>Colours of the metal; set by the theme.</summary>
    public Color BladeColor { get; set; } = Color.FromRgb(0x23, 0x28, 0x30);
    public Color EdgeColor { get; set; } = Color.FromRgb(0x3A, 0x42, 0x4E);
    public Color ScaleColor { get; set; } = Color.FromRgb(0x60, 0x69, 0x76);
    public Color RimColor { get; set; } = Color.FromRgb(0x33, 0x3A, 0x45);

    public Iris()
    {
        Theme.Changed += Recolour;
        Loaded += (_, _) => Recolour();
    }

    void Recolour()
    {
        if (Application.Current?.Resources["Blade"] is not SolidColorBrush) return;
        BladeColor = Theme.ColorOf("Blade");
        EdgeColor = Theme.ColorOf("BladeEdge");
        ScaleColor = Theme.ColorOf("TextFaint");
        RimColor = Theme.ColorOf("LineStrong");
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        bool scale = ShowScale && size >= 40;
        double R = size / 2;
        double rb = scale ? R * 0.80 : R * 0.96; // blade housing
        int n = Math.Max(5, Blades);
        double ap = Math.Clamp(Aperture, 0, 1.05);
        var core = Core;

        // glow around the rim while recording
        if (Glow > 0.01)
        {
            var halo = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(110 * Glow), core.R, core.G, core.B), rb / R * 0.5 + 0.0),
                    new GradientStop(Color.FromArgb(0, core.R, core.G, core.B), 1.0),
                },
            };
            dc.DrawEllipse(halo, null, c, R * 1.08, R * 1.08);
        }

        // the engraved scale: 72 ticks, every sixth longer, like an aperture ring
        if (scale)
        {
            var tick = new Pen(new SolidColorBrush(ScaleColor), Math.Max(1, size / 220));
            var tickLong = new Pen(new SolidColorBrush(ScaleColor), Math.Max(1.2, size / 160));
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(RimColor), Math.Max(1, size / 180)), c, R * 0.995, R * 0.995);
            for (int i = 0; i < 72; i++)
            {
                double a = (i * 5 + ScaleTurn) * Math.PI / 180;
                bool major = i % 6 == 0;
                double r0 = major ? R * 0.86 : R * 0.895, r1 = R * 0.95;
                dc.DrawLine(major ? tickLong : tick, new Point(c.X + Math.Cos(a) * r0, c.Y + Math.Sin(a) * r0), new Point(c.X + Math.Cos(a) * r1, c.Y + Math.Sin(a) * r1));
            }
        }

        // the housing behind the blades
        dc.DrawEllipse(new SolidColorBrush(BladeColor), new Pen(new SolidColorBrush(RimColor), Math.Max(1, size / 140)), c, rb, rb);

        // the opening: a regular polygon that turns as it closes
        double r = rb * (0.06 + 0.86 * ap);
        double theta = (Turn + (1 - ap) * 58) * Math.PI / 180 - Math.PI / 2;
        var v = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double a = theta + i * 2 * Math.PI / n;
            v[i] = new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
        }

        // light through the lens
        var hole = new StreamGeometry();
        using (var g = hole.Open())
        {
            g.BeginFigure(v[0], true, true);
            for (int i = 1; i < n; i++) g.LineTo(v[i], true, true);
        }
        hole.Freeze();
        var light = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.42, 0.38), RadiusX = 0.62, RadiusY = 0.62,
            GradientStops =
            {
                new GradientStop(Lighten(core, 0.55), 0),
                new GradientStop(core, 0.45),
                new GradientStop(Darken(core, 0.55), 1),
            },
        };
        dc.PushClip(new EllipseGeometry(c, rb, rb));
        dc.DrawEllipse(light, null, c, r * 1.02, r * 1.02);

        // the blades: each is bounded by two of the opening's sides carried on out to the housing
        var e = new Point[n];
        for (int j = 0; j < n; j++)
        {
            var from = v[(j - 1 + n) % n];
            var dir = new Vector(v[j].X - from.X, v[j].Y - from.Y);
            dir.Normalize();
            e[j] = RayToCircle(v[j], dir, c, rb * 1.02);
        }
        var edgePen = new Pen(new SolidColorBrush(EdgeColor), Math.Max(0.8, size / 260));
        for (int i = 0; i < n; i++)
        {
            int k = (i + 1) % n;
            var blade = new StreamGeometry();
            using (var g = blade.Open())
            {
                g.BeginFigure(v[i], true, true);
                g.LineTo(v[k], true, false);
                g.LineTo(e[k], true, false);
                g.ArcTo(e[i], new Size(rb * 1.02, rb * 1.02), 0, false, SweepDirection.Counterclockwise, true, false);
            }
            blade.Freeze();
            // light falls from the top left: blades facing it are a touch brighter
            double mid = Math.Atan2((v[i].Y + v[k].Y) / 2 - c.Y, (v[i].X + v[k].X) / 2 - c.X);
            double lit = 0.5 + 0.5 * Math.Cos(mid + Math.PI * 0.75);
            var shade = new LinearGradientBrush(Mix(BladeColor, EdgeColor, 0.15 + 0.75 * lit), Mix(BladeColor, Colors.Black, 0.18 * (1 - lit)),
                new Point(0.5 + 0.5 * Math.Cos(mid + Math.PI), 0.5 + 0.5 * Math.Sin(mid + Math.PI)), new Point(0.5 + 0.5 * Math.Cos(mid), 0.5 + 0.5 * Math.Sin(mid)));
            dc.DrawGeometry(shade, null, blade);
            dc.DrawLine(edgePen, v[k], e[k]);
        }
        dc.Pop();
        // the blades cast a soft shadow onto the light, and their sharp inner edges catch it
        dc.PushClip(hole);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(48, 0, 0, 0)), Math.Max(2, size / 36)), hole);
        dc.Pop();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(170, core.R, core.G, core.B)), Math.Max(0.8, size / 240)), hole);

        // a glint on the glass
        if (r > size * 0.06)
        {
            var glint = new RadialGradientBrush(Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
            dc.PushClip(hole);
            dc.DrawEllipse(glint, null, new Point(c.X - r * 0.32, c.Y - r * 0.36), r * 0.32, r * 0.22);
            dc.Pop();
        }
    }

    static Point RayToCircle(Point p, Vector d, Point c, double radius)
    {
        // p + t·d on the circle, t > 0
        double fx = p.X - c.X, fy = p.Y - c.Y;
        double b = 2 * (fx * d.X + fy * d.Y), cc = fx * fx + fy * fy - radius * radius;
        double disc = Math.Max(0, b * b - 4 * cc);
        double t = (-b + Math.Sqrt(disc)) / 2;
        return new Point(p.X + d.X * t, p.Y + d.Y * t);
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);
    static Color Darken(Color c, double t) => Mix(c, Colors.Black, t);
}
