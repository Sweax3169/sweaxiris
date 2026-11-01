using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SweaxIris.UI;

/// <summary>
/// Colour tokens as application resources (DynamicResource), for light and dark and six lens-coating colours.
/// The neutrals are a cool optical grey; the coating colour is the accent, tally red always means "recording".
/// </summary>
public static class Theme
{
    public sealed record Palette(string Key, Color Dark, Color Light);

    public static readonly Palette[] Palettes =
    [
        new("coating", Rgb(0x2B, 0xD4, 0xB4), Rgb(0x0B, 0x93, 0x7C)),
        new("cobalt", Rgb(0x72, 0x96, 0xFF), Rgb(0x2C, 0x5B, 0xE0)),
        new("ember", Rgb(0xFF, 0x86, 0x52), Rgb(0xD4, 0x52, 0x1C)),
        new("orchid", Rgb(0xCF, 0x82, 0xFF), Rgb(0x97, 0x3C, 0xD4)),
        new("lime", Rgb(0xA9, 0xE0, 0x4C), Rgb(0x5A, 0x95, 0x10)),
        new("graphite", Rgb(0xBF, 0xC6, 0xD0), Rgb(0x46, 0x50, 0x5E)),
    ];

    public static bool Dark { get; private set; } = true;
    public static Palette Current { get; private set; } = Palettes[0];
    public static event Action? Changed;

    static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    static Color A(Color c, double a) => Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B);

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }

    public static void Apply(string mode, string palette)
    {
        Dark = mode switch { "light" => false, "dark" => true, _ => SystemIsDark() };
        Current = Palettes.FirstOrDefault(p => p.Key == palette) ?? Palettes[0];
        var accent = Dark ? Current.Dark : Current.Light;
        var r = Application.Current.Resources;

        Color bg, surface, raised, sunken, line, lineStrong, text, dim, faint, blade, bladeEdge;
        if (Dark)
        {
            bg = Rgb(0x0B, 0x0D, 0x10); surface = Rgb(0x13, 0x16, 0x1B); raised = Rgb(0x1B, 0x1F, 0x26); sunken = Rgb(0x0E, 0x10, 0x14);
            line = Rgb(0x24, 0x29, 0x31); lineStrong = Rgb(0x33, 0x3A, 0x45);
            text = Rgb(0xE9, 0xEC, 0xF1); dim = Rgb(0x9A, 0xA3, 0xB0); faint = Rgb(0x60, 0x69, 0x76);
            blade = Rgb(0x23, 0x28, 0x30); bladeEdge = Rgb(0x3A, 0x42, 0x4E);
        }
        else
        {
            bg = Rgb(0xE8, 0xEB, 0xEF); surface = Rgb(0xF6, 0xF7, 0xF9); raised = Rgb(0xFF, 0xFF, 0xFF); sunken = Rgb(0xDF, 0xE3, 0xE8);
            line = Rgb(0xD3, 0xD8, 0xDF); lineStrong = Rgb(0xBD, 0xC4, 0xCE);
            text = Rgb(0x12, 0x15, 0x1A); dim = Rgb(0x4E, 0x57, 0x64); faint = Rgb(0x87, 0x8F, 0x9B);
            blade = Rgb(0x2A, 0x2F, 0x37); bladeEdge = Rgb(0x48, 0x50, 0x5C);
        }
        var tally = Dark ? Rgb(0xFF, 0x3D, 0x5E) : Rgb(0xE3, 0x1F, 0x43);
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;

        Set(r, "Bg", bg);
        Set(r, "Surface", surface);
        Set(r, "Raised", raised);
        Set(r, "Sunken", sunken);
        Set(r, "Hover", Dark ? A(Colors.White, 0.06) : A(Colors.Black, 0.05));
        Set(r, "Press", Dark ? A(Colors.White, 0.10) : A(Colors.Black, 0.09));
        Set(r, "Line", line);
        Set(r, "LineStrong", lineStrong);
        Set(r, "Text", text);
        Set(r, "TextDim", dim);
        Set(r, "TextFaint", faint);
        Set(r, "Accent", accent);
        Set(r, "AccentHover", Mix(accent, Dark ? Colors.White : Colors.Black, 0.12));
        Set(r, "AccentText", lum > 0.55 ? Rgb(0x08, 0x0B, 0x0E) : Colors.White);
        Set(r, "AccentSoft", A(accent, Dark ? 0.16 : 0.13));
        Set(r, "AccentLine", A(accent, 0.55));
        Set(r, "Tally", tally);
        Set(r, "TallySoft", A(tally, 0.18));
        Set(r, "MeterLow", Dark ? Rgb(0x3D, 0xD6, 0x8C) : Rgb(0x1E, 0xA8, 0x63));
        Set(r, "MeterMid", Dark ? Rgb(0xF2, 0xC9, 0x4C) : Rgb(0xC9, 0x96, 0x10));
        Set(r, "MeterHigh", tally);
        Set(r, "Blade", blade);
        Set(r, "BladeEdge", bladeEdge);
        Set(r, "Veil", A(Rgb(0x05, 0x07, 0x0A), 0.52));
        Set(r, "Shadow", A(Colors.Black, Dark ? 0.55 : 0.18));
        Set(r, "Viewfinder", Dark ? Rgb(0x07, 0x08, 0x0A) : Rgb(0x1A, 0x1E, 0x24));
        r["AccentColor"] = accent;
        r["TallyColor"] = tally;
        r["BgColor"] = bg;
        r["SurfaceColor"] = surface;
        r["ShadowColor"] = A(Colors.Black, Dark ? 0.6 : 0.22);
        Changed?.Invoke();
    }

    static void Set(ResourceDictionary r, string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        r[key] = b;
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static Color ColorOf(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;
}
