using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using WyvernCenter.Core.Features;
using WyvernCenter.UI;

namespace WyvernCenter;

/// <summary>
/// The in-game overlay: a few lines of readings in a screen corner. Per-pixel alpha (UpdateLayeredWindow) so only
/// the background fades with the opacity setting and the text stays solid. Never takes focus, lets every click
/// through, stays top-most and out of the taskbar / Alt+Tab.
/// </summary>
public sealed class OverlayForm : Form
{
    private const int WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10, SwpShowWindow = 0x40;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // No TopMost here: WinForms focuses a TopMost form when it is shown, which would take the keyboard from the
        // game. WS_EX_TOPMOST (CreateParams) plus SetWindowPos(…, SWP_NOACTIVATE) keeps it on top without that.
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExTopmost | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
            return cp;
        }
    }

    /// <param name="opacityPct">Background opacity 0–90 %; text is always opaque.</param>
    /// <param name="corner">0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right of the primary screen.</param>
    public void Render(IReadOnlyList<OverlayLine> lines, int opacityPct, int corner)
    {
        if (IsDisposed) return;
        _ = Handle;
        var scale = DeviceDpi / 96f * Theme.UserScale;
        using var labelFont = new Font("Segoe UI", 10f * Theme.UserScale, FontStyle.Bold);
        using var valueFont = new Font("Segoe UI", 10f * Theme.UserScale);
        using var bigFont = new Font("Segoe UI", 15f * Theme.UserScale, FontStyle.Bold);
        int pad = (int)(8 * scale), gap = (int)(10 * scale), lineH = (int)(19 * scale), bigH = (int)(27 * scale), graphH = (int)(30 * scale);
        int RowH(OverlayLine l) => l.Graph is not null ? graphH : l.Big ? bigH : lineH;

        // Measure: label column + widest value (a graph is at least this wide).
        int labelW = 0, valueW = (int)(170 * scale);
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            foreach (var l in lines)
            {
                labelW = Math.Max(labelW, (int)Math.Ceiling(g.MeasureString(l.Label, labelFont).Width));
                if (l.Graph is null) valueW = Math.Max(valueW, (int)Math.Ceiling(g.MeasureString(l.Text, l.Big ? bigFont : valueFont).Width));
            }
        }
        var width = Math.Max(1, pad * 2 + labelW + gap + valueW);
        var height = Math.Max(1, pad * 2 + Math.Max(lineH, lines.Sum(RowH)));

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var alpha = Math.Clamp(opacityPct, 0, 90) * 255 / 100;
            if (alpha > 0)
            {
                using var back = new SolidBrush(Color.FromArgb(alpha, 12, 12, 12));
                using var path = Theme.RoundRect(new RectangleF(0, 0, width, height), 8 * scale);
                g.FillPath(back, path);
            }
            using var shadow = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
            using var white = new SolidBrush(Color.White);
            var y = pad;
            foreach (var line in lines)
            {
                var h = RowH(line);
                using var labelBrush = new SolidBrush(RoleColor(line.Role));
                if (line.Graph is { } values)
                    DrawGraph(g, values, new RectangleF(pad + labelW + gap, y + 2 * scale, valueW, h - 6 * scale), scale);
                else
                {
                    var font = line.Big ? bigFont : valueFont;
                    // Labels sit on the value's baseline row: vertically centred in a big row.
                    DrawShadowed(g, line.Label, labelFont, labelBrush, shadow, pad, y + (h - lineH) / 2f);
                    DrawShadowed(g, line.Text, font, white, shadow, pad + labelW + gap, y + (line.Big ? 0 : 0));
                }
                y += h;
            }
        }

        var screen = Screen.PrimaryScreen!.Bounds;
        var margin = (int)(12 * scale);
        var x = corner is 1 or 3 ? screen.Right - width - margin : screen.Left + margin;
        var top = corner is 2 or 3 ? screen.Bottom - height - margin : screen.Top + margin;
        Push(bmp, new Point(x, top));
        if (!Visible) Show();
        SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow); // games can push it down
    }

    /// <summary>The last minute of FPS as a filled sparkline, scaled to its own peak, with that peak written at the right.</summary>
    private static void DrawGraph(Graphics g, IReadOnlyList<double?> values, RectangleF r, float scale)
    {
        var peak = Math.Max(1, values.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(1).Max());
        using (var baseLine = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
            g.DrawLine(baseLine, r.Left, r.Bottom, r.Right, r.Bottom);
        var step = r.Width / Math.Max(1, values.Count - 1);
        var pts = new List<PointF>();
        void Flush()
        {
            if (pts.Count > 1)
            {
                using var fill = new SolidBrush(Color.FromArgb(70, Theme.Accent));
                var poly = new List<PointF>(pts) { new(pts[^1].X, r.Bottom), new(pts[0].X, r.Bottom) };
                g.FillPolygon(fill, poly.ToArray());
                using var line = new Pen(Theme.Accent, 1.6f * scale);
                g.DrawLines(line, pts.ToArray());
            }
            pts.Clear();
        }
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not double v) { Flush(); continue; }
            pts.Add(new PointF(r.Left + i * step, r.Bottom - r.Height * (float)Math.Clamp(v / peak, 0, 1)));
        }
        Flush();
        using var small = new Font("Segoe UI", 7.5f * Theme.UserScale);
        using var dim = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        var text = $"{peak:F0}";
        var size = g.MeasureString(text, small);
        g.DrawString(text, small, dim, r.Right - size.Width, r.Top - 2 * scale);
    }

    public static Color RoleColor(OverlayRole role) => role switch
    {
        OverlayRole.Cpu => Theme.Cpu,
        OverlayRole.Gpu => Theme.Gpu,
        OverlayRole.Power => Color.FromArgb(255, 190, 60),
        OverlayRole.Sys => Theme.ForeDim,
        _ => Theme.Accent,
    };

    private static void DrawShadowed(Graphics g, string text, Font font, Brush brush, Brush shadow, float x, float y)
    {
        g.DrawString(text, font, shadow, x + 1, y + 1);
        g.DrawString(text, font, brush, x, y);
    }

    private void Push(Bitmap bmp, Point location)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = location.X, y = location.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2); // ULW_ALPHA
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
