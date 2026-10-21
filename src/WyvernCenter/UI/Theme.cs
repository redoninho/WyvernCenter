using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WyvernCenter.UI;

/// <summary>Palette matching G-Helper's dark theme.</summary>
public static class Theme
{
    // Accents
    public static readonly Color Eco = Color.FromArgb(6, 180, 138);
    public static readonly Color Standard = Color.FromArgb(58, 174, 239);
    public static readonly Color Turbo = Color.FromArgb(255, 32, 32);
    public static readonly Color Custom = Color.FromArgb(255, 128, 0);
    public static readonly Color Gray = Color.FromArgb(168, 168, 168);

    // Colour roles (one meaning each): every normal control uses Accent; blue is only for CPU things, red only for
    // GPU things; Warn is for warning/error text.
    public static readonly Color Accent = Color.FromArgb(232, 17, 35);    // MSI red
    public static readonly Color Cpu = Color.FromArgb(58, 174, 239);
    public static readonly Color Gpu = Color.FromArgb(230, 40, 40);
    public static readonly Color InUse = Color.FromArgb(6, 200, 120);     // "in use" marker
    public static readonly Color Warn = Color.FromArgb(255, 150, 40);

    // Surfaces
    public static readonly Color ButtonMain = Color.FromArgb(46, 46, 46);
    public static readonly Color ButtonSecond = Color.FromArgb(36, 36, 36);
    public static readonly Color FormBack = Color.FromArgb(28, 28, 28);
    public static readonly Color ForeMain = Color.FromArgb(240, 240, 240);
    public static readonly Color ForeDim = Color.FromArgb(150, 150, 150);
    public static readonly Color BorderMain = Color.FromArgb(55, 55, 55);
    public static readonly Color BorderSecond = Color.FromArgb(42, 42, 42);
    public static readonly Color Track = Color.FromArgb(70, 70, 70);

    public static readonly string IconFontName =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    /// <summary>User interface scale on top of Windows DPI scaling (Extra settings → UI scale).</summary>
    public static float UserScale { get; set; } = 1f;

    /// <summary>Pixels per layout unit for a control: monitor DPI times the user scale.</summary>
    public static float Px(Control c) => c.DeviceDpi / 96f * UserScale;

    /// <summary>Scale a 96-DPI layout value by the user scale (DPI is handled by WinForms autoscaling).</summary>
    public static int S(int value) => (int)Math.Round(value * UserScale);

    public static Font IconFont(float size) => new(IconFontName, size * UserScale);
    public static Font UiFont(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size * UserScale, style);

    public static Color Lighten(Color c, int amount) =>
        Color.FromArgb(c.A, Math.Min(255, c.R + amount), Math.Min(255, c.G + amount), Math.Min(255, c.B + amount));

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Dark scrollbars and common-control chrome.</summary>
    public static void DarkControl(Control c) =>
        c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);

    public static void DarkTitleBar(IntPtr handle)
    {
        int on = 1;
        DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}

/// <summary>Segoe Fluent Icons codepoints.</summary>
/// <summary>Text helpers.</summary>
public static class TextWrap
{
    /// <summary>Breaks long tooltip text into lines of about <paramref name="width"/> characters (keeps its own line breaks).</summary>
    public static string Lines(string text, int width = 58)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var line = 0;
            foreach (var word in para.Split(' '))
            {
                if (word.Length == 0) continue;
                if (line > 0 && line + 1 + word.Length > width) { sb.Append('\n'); line = 0; }
                else if (line > 0) { sb.Append(' '); line++; }
                sb.Append(word);
                line += word.Length;
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}

public static class Glyphs
{
    public const string Sun = "\uE706";
    public const string Moon = "\uE708";
    public const string Palette = "\uE790";
    public const string Monitor = "\uE7F4";
    public const string Star = "\uE734";
    public const string RedEye = "\uE7B3";
    public const string Eye = "\uE890";
    public const string Picture = "\uE91B";
    public const string Contrast = "\uE793";
    public const string Thermometer = "\uE9CA";
    public const string Photos = "\uE8B9";
    public const string Brush = "\uE771";
    public const string Bulb = "\uEA80";
    public const string Drop = "\uEB42";
    public const string Layers = "\uE7C4";
    public const string Bolt = "\uE945";
    public const string Warning = "\uE7BA";
    public const string Sunset = "\uED39";
    public const string Check = "\uE73E";
    public const string ChevronDown = "\uE70D";
    public const string Refresh = "\uE72C";
    public const string Pause = "\uE769";
    public const string Play = "\uE768";
    public const string Power = "\uE7E8";
    public const string Gear = "\uE713";
    public const string Screen = "\uE7F7";
    public const string Clock = "\uE823";
    public const string Keyboard = "\uE765";
    public const string Shield = "\uE83D";
    public const string Add = "\uE710";
    public const string Folder = "\uE838";
    public const string Save = "\uE74E";
    public const string Battery = "\uE83F";
    public const string Camera = "\uE722";
    public const string Info = "\uE946";
}
