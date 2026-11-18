using System.Drawing.Drawing2D;

namespace WyvernCenter.UI;

/// <summary>Rounded G-Helper style button; shows a coloured border when <see cref="Activated"/>.</summary>
public class RButton : Button
{
    bool activated, hover, down;

    public bool Activated
    {
        get => activated;
        set { if (activated != value) { activated = value; Invalidate(); } }
    }

    public Color BorderColor { get; set; } = Theme.Accent;
    public string? Glyph { get; set; }
    /// <summary>Colour of the glyph (e.g. the profile's colour); text keeps the normal colour.</summary>
    public Color? GlyphColor { get; set; }
    public bool Secondary { get; set; }

    /// <summary>Small G-Helper "FN-Lock" style badge: filled with the accent colour when activated.</summary>
    public bool Pill { get; set; }

    /// <summary>Own fill and text colours for a pill (brand-coloured link buttons); the fill lightens on hover.</summary>
    public Color? PillBack { get; set; }
    public Color? PillFore { get; set; }

    public RButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Theme.ForeMain;
        BackColor = Theme.ButtonMain;
        Font = Theme.UiFont(10.5f);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = Theme.Px(this);
        g.Clear(Parent?.BackColor ?? Theme.FormBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color back = Secondary ? Theme.ButtonSecond : Theme.ButtonMain;
        if (Pill && activated) back = BorderColor;
        if (Pill && PillBack is Color own) back = own;
        if (down) back = Theme.Lighten(back, 18);
        else if (hover) back = Theme.Lighten(back, 10);

        if (Pill)
        {
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 4 * s))
            using (var brush = new SolidBrush(back))
                g.FillPath(brush, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, PillFore ?? (activated ? Color.White : Theme.ForeDim), back,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Theme.RoundRect(rect, 5 * s))
        using (var brush = new SolidBrush(back))
            g.FillPath(brush, path);

        if (activated)
        {
            float w = 2 * s;
            var inner = new RectangleF(w / 2 + 0.5f, w / 2 + 0.5f, Width - w - 1, Height - w - 1);
            using var path = Theme.RoundRect(inner, 4.5f * s);
            using var pen = new Pen(BorderColor, w);
            g.DrawPath(pen, path);
        }

        Color fore = Enabled ? ForeColor : Theme.ForeDim;
        Color glyphFore = Enabled ? GlyphColor ?? fore : Theme.ForeDim;
        bool stacked = Glyph != null && Height > 44 * s;

        if (stacked)
        {
            using var icon = Theme.IconFont(Height > 58 * s ? 14f : 11.5f);
            var top = new Rectangle(0, (int)(Height * 0.10f), Width, (int)(Height * 0.44f));
            var bottom = new Rectangle(0, (int)(Height * 0.50f), Width, (int)(Height * 0.40f));
            TextRenderer.DrawText(g, Glyph, icon, top, glyphFore, back, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Text, Font, bottom, fore, back, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else if (Glyph != null)
        {
            using var icon = Theme.IconFont(11f);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            var gs = TextRenderer.MeasureText(g, Glyph, icon, Size.Empty, flags);
            var ts = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
            int gap = (int)(8 * s);
            int x = (Width - gs.Width - gap - ts.Width) / 2;
            TextRenderer.DrawText(g, Glyph, icon, new Rectangle(x, 0, gs.Width, Height), glyphFore, back, flags);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + gs.Width + gap, 0, ts.Width + 2, Height), fore, back, flags);
        }
        else
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore, back,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
