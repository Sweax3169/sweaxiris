using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SweaxIris.Dev;

/// <summary>Renders WPF elements to PNG files (for checking the look, and for the README pictures).</summary>
public static class Snap
{
    public static BitmapSource Render(FrameworkElement el, double width, double height, double scale = 2)
    {
        el.Measure(new Size(width, height));
        el.Arrange(new Rect(0, 0, width, height));
        el.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(el);
        bmp.Freeze();
        return bmp;
    }

    public static void Save(BitmapSource bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>--fonts FILE: which way of asking for the Plex weights works.</summary>
    public static void Fonts(string file)
    {
        var sp = new System.Windows.Controls.StackPanel { Background = Brushes.White };
        void Line(string label, FontFamily f, FontWeight w) => sp.Children.Add(new System.Windows.Controls.TextBlock { Text = label + "  Hamburgefonstiv ğşıİ 0123", FontFamily = f, FontWeight = w, FontSize = 22, Margin = new Thickness(8, 2, 8, 2) });
        var plex = (FontFamily)Application.Current.FindResource("Plex");
        foreach (var (n, w) in new[] { ("Light", FontWeights.Light), ("Regular", FontWeights.Normal), ("Medium", FontWeights.Medium), ("SemiBold", FontWeights.SemiBold), ("Bold", FontWeights.Bold) }) Line("family+" + n, plex, w);
        Line("PlexMedium key", (FontFamily)Application.Current.FindResource("PlexMedium"), FontWeights.Normal);
        Line("PlexSemi key", (FontFamily)Application.Current.FindResource("PlexSemi"), FontWeights.Normal);
        Line("PlexLight key", (FontFamily)Application.Current.FindResource("PlexLight"), FontWeights.Normal);
        Line("Segoe UI", new FontFamily("Segoe UI"), FontWeights.Normal);
        Save(Render(sp, 900, 420, 1), file);
    }

    /// <summary>--iris DIR: the iris at a few openings on both themes.</summary>
    public static void Irises(string dir)
    {
        foreach (var dark in new[] { true, false })
        {
            UI.Theme.Apply(dark ? "dark" : "light", "coating");
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = UI.Theme.Brush("Bg") };
            foreach (var (ap, glow, core) in new[] { (0.0, 0.0, "AccentColor"), (0.2, 0.0, "AccentColor"), (0.45, 0.0, "AccentColor"), (0.7, 0.6, "TallyColor"), (0.95, 1.0, "TallyColor") })
            {
                var iris = new UI.Iris { Width = 200, Height = 200, Margin = new Thickness(16), Aperture = ap, Glow = glow, Core = (Color)Application.Current.Resources[core] };
                row.Children.Add(iris);
            }
            Save(Render(row, 5 * 232, 232), Path.Combine(dir, $"iris-{(dark ? "dark" : "light")}.png"));
        }
    }

    /// <summary>An iris for icons: dark housing, lit coating, no scale when small.</summary>
    static BitmapSource IrisBitmap(int size, Color core, double aperture = 0.5)
    {
        var iris = new UI.Iris { Width = size, Height = size, Aperture = aperture, Core = core, ShowScale = size >= 96 };
        iris.BladeColor = Color.FromRgb(0x23, 0x28, 0x30);
        iris.EdgeColor = Color.FromRgb(0x4A, 0x53, 0x60);
        iris.RimColor = Color.FromRgb(0x5A, 0x63, 0x70);
        iris.ScaleColor = Color.FromRgb(0x8A, 0x93, 0xA0);
        var host = new System.Windows.Controls.Grid { Width = size, Height = size };
        if (size >= 96)
            host.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x17)) });
        host.Children.Add(iris);
        return Render(host, size, size, 1);
    }

    /// <summary>--icon DIR: the app icon at every size, plus sweaxiris.ico.</summary>
    public static void Icon(string dir)
    {
        Directory.CreateDirectory(dir);
        var core = Color.FromRgb(0x2B, 0xD4, 0xB4);
        var pngs = new List<byte[]>();
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        foreach (int s in sizes)
        {
            var bmp = IrisBitmap(s, core, 0.5);
            Save(bmp, Path.Combine(dir, $"icon-{s}.png"));
            pngs.Add(File.ReadAllBytes(Path.Combine(dir, $"icon-{s}.png")));
        }
        Save(IrisBitmap(512, core, 0.5), Path.Combine(dir, "icon-512.png"));
        Save(IrisBitmap(1024, core, 0.5), Path.Combine(dir, "icon-1024.png"));
        using var fs = File.Create(Path.Combine(dir, "sweaxiris.ico"));
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, byte[]? bits);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    /// <summary>The tray icon while recording: the iris wide open in tally red.</summary>
    public static IntPtr TrayIcon()
    {
        int size = (int)Math.Round(16 * VisualTreeHelperDpi());
        size = Math.Clamp(size, 16, 32);
        var bmp = IrisBitmap(size, (Color)Application.Current.Resources["TallyColor"], 0.9);
        var px = new byte[size * size * 4];
        bmp.CopyPixels(px, size * 4, 0);
        // un-premultiply for the icon
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a is > 0 and < 255) for (int c = 0; c < 3; c++) px[i + c] = (byte)Math.Min(255, px[i + c] * 255 / a);
        }
        var info = new ICONINFO { fIcon = true, hbmColor = CreateBitmap(size, size, 1, 32, px), hbmMask = CreateBitmap(size, size, 1, 1, new byte[size * size / 8 + 64]) };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(info.hbmColor);
        DeleteObject(info.hbmMask);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForSystem();
    static double VisualTreeHelperDpi() => GetDpiForSystem() / 96.0;
}
