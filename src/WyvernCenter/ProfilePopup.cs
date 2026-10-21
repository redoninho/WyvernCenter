using System.Drawing.Drawing2D;
using WyvernCenter.Core.Features;
using WyvernCenter.UI;

namespace WyvernCenter;

/// <summary>
/// Small note at the bottom of the screen after the hotkey switches profile. It never takes focus and never catches
/// clicks (layered + transparent + no-activate), so games and typing carry on underneath it.
/// </summary>
public sealed class ProfilePopup : Form
{
    private const int WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    private const int HoldTicks = 30; // 30 × 50 ms = 1.5 s before fading
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 50 };
    private int hold;
    private string glyph = "", title = "", line = "";
    private Color accent = Theme.ForeMain;

    public ProfilePopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.ButtonMain;
        DoubleBuffered = true;
        timer.Tick += (_, _) => Step();
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

    /// <param name="profile">The profile switched to (its glyph and colour), or null for a plain message.</param>
    public void ShowMessage(Profile? profile, string heading, string detail)
    {
        glyph = profile is null ? Glyphs.Gear : MainForm.GlyphFor(profile);
        accent = profile is null ? Theme.ForeDim : AppController.ProfileColor(profile);
        title = heading;
        line = detail;

        _ = Handle; // DeviceDpi is known once the window exists
        var scale = DeviceDpi / 96f * Theme.UserScale;
        var size = new Size((int)(500 * scale), (int)(68 * scale));
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Bounds = new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Bottom - size.Height - (int)(40 * scale), size.Width, size.Height);
        using (var path = Theme.RoundRect(new RectangleF(0, 0, size.Width, size.Height), 10 * scale))
            Region = new Region(path);

        hold = HoldTicks;
        Opacity = 0.96;
        if (!Visible) Show();
        Invalidate();
        timer.Start();
    }

    private void Step()
    {
        if (hold > 0) { hold--; return; }
        Opacity -= 0.08;
        if (Opacity > 0.05) return;
        timer.Stop();
        Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var px = DeviceDpi / 96f * Theme.UserScale;
        int pad = (int)(16 * px), icon = (int)(36 * px);
        using var iconFont = Theme.IconFont(20f);
        TextRenderer.DrawText(g, glyph, iconFont, new Rectangle(pad, 0, icon, Height), accent,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        int x = pad + icon + (int)(12 * px), w = Width - x - pad;
        using var titleFont = Theme.UiFont(12f, FontStyle.Bold);
        using var lineFont = Theme.UiFont(9f);
        TextRenderer.DrawText(g, title, titleFont, new Rectangle(x, (int)(10 * px), w, (int)(26 * px)), Theme.ForeMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, line, lineFont, new Rectangle(x, (int)(36 * px), w, (int)(22 * px)), Theme.ForeDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
