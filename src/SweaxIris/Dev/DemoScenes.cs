using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SweaxIris.Dev;

/// <summary>
/// Made-up pictures for the README and the site, drawn in code so no real screen ever appears:
/// a racing game at golden hour, and a desktop with two app windows.
/// </summary>
public sealed class GameScene : FrameworkElement
{
    public double Time { get; set; }

    static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, t = Time;
        double horizon = h * 0.52;
        // sky
        dc.DrawRectangle(new LinearGradientBrush(new GradientStopCollection
        {
            new(C(0x1B2A4E), 0), new(C(0x5B3F7A), 0.45), new(C(0xF08A5D), 0.85), new(C(0xFFC57A), 1),
        }, 90), null, new Rect(0, 0, w, horizon + 2));
        // sun with soft halo
        var sun = new Point(w * 0.62, horizon - h * 0.07);
        dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(0x90, 0xFF, 0xD9, 0x9A), Color.FromArgb(0, 0xFF, 0xD9, 0x9A)), null, sun, h * 0.34, h * 0.34);
        dc.DrawEllipse(new SolidColorBrush(C(0xFFE3A8)), null, sun, h * 0.09, h * 0.09);
        // three ranges of mountains moving at different speeds
        void Range(double baseY, double amp, double speed, Color col, int seed)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(0, horizon + 2), true, true);
                double off = t * speed;
                for (int i = 0; i <= 48; i++)
                {
                    double x = i * w / 48;
                    double u = (x + off) / w * 6 + seed;
                    double y = baseY - amp * (0.55 + 0.45 * Math.Sin(u * 1.7) * Math.Cos(u * 0.9 + seed) + 0.25 * Math.Sin(u * 4.1));
                    c.LineTo(new Point(x, y), true, false);
                }
                c.LineTo(new Point(w, horizon + 2), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(new SolidColorBrush(col), null, g);
        }
        Range(horizon - h * 0.02, h * 0.16, 14, C(0x7E5A8C), 1);
        Range(horizon, h * 0.11, 30, C(0x51406B), 3);
        Range(horizon + 4, h * 0.06, 60, C(0x2E2846), 5);
        // ground
        dc.DrawRectangle(new LinearGradientBrush(C(0x2A2238), C(0x120F1A), 90), null, new Rect(0, horizon, w, h - horizon));
        // the road towards the sun, with dashes rushing at the camera
        double vx = sun.X, curve = Math.Sin(t * 0.6) * w * 0.08;
        var road = new StreamGeometry();
        using (var c = road.Open())
        {
            c.BeginFigure(new Point(vx - 4, horizon), true, true);
            c.LineTo(new Point(vx + 4, horizon), true, false);
            c.QuadraticBezierTo(new Point(vx + curve + w * 0.1, horizon + (h - horizon) * 0.5), new Point(w * 0.78 + curve, h), true, false);
            c.LineTo(new Point(w * 0.18 + curve, h), true, false);
            c.QuadraticBezierTo(new Point(vx + curve - w * 0.1, horizon + (h - horizon) * 0.5), new Point(vx - 4, horizon), true, false);
        }
        road.Freeze();
        dc.DrawGeometry(new LinearGradientBrush(C(0x3A3348), C(0x1E1A28), 90), null, road);
        for (int i = 0; i < 9; i++)
        {
            double z = ((i / 9.0) + t * 0.9) % 1;
            double p = z * z;
            double y = horizon + (h - horizon) * p;
            double x = vx + (w * 0.48 + curve - vx) * p + curve * p * (1 - p) * 2;
            double len = 4 + 40 * p, wid = 1 + 7 * p;
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(120 + 135 * p), 0xFF, 0xE6, 0xB0)), null, new Rect(x - wid / 2, y, wid, len));
        }
        // the car
        double carX = w * 0.48 + curve * 0.9 + Math.Sin(t * 1.3) * w * 0.02, carY = h * 0.8;
        var body = new StreamGeometry();
        using (var c = body.Open())
        {
            c.BeginFigure(new Point(carX - w * 0.07, carY + h * 0.08), true, true);
            c.LineTo(new Point(carX - w * 0.065, carY + h * 0.02), true, false);
            c.LineTo(new Point(carX - w * 0.035, carY - h * 0.02), true, false);
            c.LineTo(new Point(carX + w * 0.035, carY - h * 0.02), true, false);
            c.LineTo(new Point(carX + w * 0.065, carY + h * 0.02), true, false);
            c.LineTo(new Point(carX + w * 0.07, carY + h * 0.08), true, false);
        }
        body.Freeze();
        dc.DrawGeometry(new LinearGradientBrush(C(0x2BD4B4), C(0x0E6E62), 90), null, body);
        dc.DrawRectangle(new SolidColorBrush(C(0x10141A)), null, new Rect(carX - w * 0.03, carY - h * 0.012, w * 0.06, h * 0.025));
        double glow = 0.6 + 0.4 * Math.Sin(t * 9);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(200 * glow), 0xFF, 0x3D, 0x5E)), null, new Point(carX - w * 0.05, carY + h * 0.045), w * 0.012, h * 0.01);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(200 * glow), 0xFF, 0x3D, 0x5E)), null, new Point(carX + w * 0.05, carY + h * 0.045), w * 0.012, h * 0.01);
        // a small game HUD
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double dpi = 1.25;
        int speed = 182 + (int)(20 * Math.Sin(t * 0.8));
        var ft = new FormattedText(speed.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, h * 0.09, Brushes.White, dpi);
        dc.DrawText(ft, new Point(w * 0.04, h - h * 0.16));
        dc.DrawText(new FormattedText("km/h", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, h * 0.03, new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255)), dpi), new Point(w * 0.04 + ft.Width + 6, h - h * 0.1));
        dc.DrawText(new FormattedText($"LAP 2/3   {1 + (int)(t / 60)}:{(int)(t % 60) + 14:00}.{(int)(t * 100 % 100):00}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, h * 0.035, Brushes.White, dpi), new Point(w * 0.04, h * 0.05));
    }
}

/// <summary>A made-up desktop: a wallpaper, a dashboard app with a live chart and an editor.</summary>
public sealed class DesktopScene : FrameworkElement
{
    public double Time { get; set; }
    public Rect ChartWindow { get; private set; }

    static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, t = Time;
        // wallpaper: slow bands of colour
        dc.DrawRectangle(new LinearGradientBrush(new GradientStopCollection { new(C(0x0F2A3A), 0), new(C(0x1D4E5F), 0.5), new(C(0x2D6E6A), 1) }, 35), null, new Rect(0, 0, w, h));
        for (int i = 0; i < 4; i++)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                double y0 = h * (0.35 + i * 0.14);
                c.BeginFigure(new Point(0, h), true, true);
                for (int k = 0; k <= 24; k++)
                {
                    double x = k * w / 24;
                    c.LineTo(new Point(x, y0 + Math.Sin(x / w * 5 + i + t * 0.2) * h * 0.04), true, false);
                }
                c.LineTo(new Point(w, h), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(28 + i * 10), 0xA8, 0xF0, 0xE0)), null, g);
        }
        // a plain bar along the bottom
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xE0, 0x11, 0x15, 0x1B)), null, new Rect(0, h - h * 0.05, w, h * 0.05));
        for (int i = 0; i < 6; i++)
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), null, new Rect(w / 2 - 3 * h * 0.04 + i * h * 0.04, h - h * 0.042, h * 0.03, h * 0.03), 4, 4);

        // editor window (behind)
        var ed = new Rect(w * 0.04, h * 0.07, w * 0.46, h * 0.6);
        Window(dc, ed, "notes.md — Quill", C(0x1A1E26));
        var colours = new[] { C(0x7AA2F7), C(0xC3E88D), C(0xF78C6C), C(0x89DDFF), C(0xC792EA) };
        var rng = new Random(4);
        for (int line = 0; line < 16; line++)
        {
            double x = ed.Left + 16 + (line % 4 == 0 ? 0 : 18), y = ed.Top + 44 + line * (ed.Height - 60) / 16;
            for (int k = 0; k < 4; k++)
            {
                double len = 20 + rng.NextDouble() * 70;
                if (x + len > ed.Right - 16) break;
                dc.DrawRoundedRectangle(new SolidColorBrush(colours[rng.Next(colours.Length)]), null, new Rect(x, y, len, 6), 3, 3);
                x += len + 8;
            }
        }
        // dashboard window with a live chart (in front)
        var dash = new Rect(w * 0.38, h * 0.2, w * 0.56, h * 0.64);
        ChartWindow = dash;
        Window(dc, dash, "Atlas — Live dashboard", C(0xF6F7F9));
        var plot = new Rect(dash.Left + 24, dash.Top + 70, dash.Width - 48, dash.Height - 100);
        for (int i = 0; i <= 4; i++)
            dc.DrawLine(new Pen(new SolidColorBrush(C(0xE3E6EA)), 1), new Point(plot.Left, plot.Top + plot.Height * i / 4), new Point(plot.Right, plot.Top + plot.Height * i / 4));
        int bars = 18;
        for (int i = 0; i < bars; i++)
        {
            double v = Math.Max(0.06, 0.45 + 0.25 * Math.Sin(i * 0.7 + t * 1.4) + 0.15 * Math.Cos(i * 1.9 - t));
            double bw = plot.Width / bars * 0.6;
            double x = plot.Left + i * plot.Width / bars + bw * 0.3;
            dc.DrawRoundedRectangle(new SolidColorBrush(C(0x9EB8F5)), null, new Rect(x, plot.Bottom - plot.Height * v, bw, plot.Height * v), 3, 3);
        }
        var line2 = new StreamGeometry();
        using (var c = line2.Open())
        {
            for (int i = 0; i <= 60; i++)
            {
                double x = plot.Left + plot.Width * i / 60;
                double v = 0.55 + 0.25 * Math.Sin(i * 0.25 + t * 1.8) + 0.1 * Math.Sin(i * 0.9 - t * 2.6);
                var p = new Point(x, plot.Bottom - plot.Height * v);
                if (i == 0) c.BeginFigure(p, false, false); else c.LineTo(p, true, true);
            }
        }
        line2.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(C(0x2C5BE0)), 2.5) { LineJoin = PenLineJoin.Round }, line2);
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        dc.DrawText(new FormattedText($"Active users  {12_480 + (int)(t * 37):N0}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 15, new SolidColorBrush(C(0x1A1E26)), 1.25), new Point(dash.Left + 24, dash.Top + 40));
    }

    static void Window(DrawingContext dc, Rect r, string title, Color body)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), null, new Rect(r.X + 2, r.Y + 6, r.Width, r.Height), 10, 10);
        dc.DrawRoundedRectangle(new SolidColorBrush(body), null, r, 9, 9);
        bool light = body.R > 128;
        var bar = new Rect(r.X, r.Y, r.Width, 30);
        dc.PushClip(new RectangleGeometry(r, 9, 9));
        dc.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(0xE9, 0xEC, 0xF0) : Color.FromRgb(0x23, 0x28, 0x31)), null, bar);
        dc.Pop();
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        dc.DrawText(new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 12.5, new SolidColorBrush(light ? Color.FromRgb(0x30, 0x35, 0x3C) : Color.FromRgb(0xC9, 0xCF, 0xD8)), 1.25), new Point(r.X + 14, r.Y + 7));
        for (int i = 0; i < 3; i++)
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x70, 0x80, 0x88, 0x94)), null, new Point(r.Right - 20 - i * 20, r.Y + 15), 5, 5);
    }
}
