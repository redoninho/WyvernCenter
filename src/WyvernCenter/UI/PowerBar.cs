using System.Drawing.Drawing2D;
using WyvernCenter.Core.Features;

namespace WyvernCenter.UI;

/// <summary>Stacked bar of where the watts go, with a legend underneath ("~" marks estimates).</summary>
public sealed class PowerBar : Control
{
    private PowerTotal? total;

    public PowerBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        ForeColor = Theme.ForeMain;
    }

    public PowerTotal? Total
    {
        get => total;
        set { total = value; Invalidate(); }
    }

    public static Color PartColor(PowerPart p) => p switch
    {
        PowerPart.Cpu => Theme.Cpu,
        PowerPart.Gpu => Theme.Gpu,
        PowerPart.Memory => Color.FromArgb(150, 110, 255),
        PowerPart.Screen => Color.FromArgb(255, 190, 60),
        PowerPart.Keyboard => Color.FromArgb(255, 105, 180),
        PowerPart.Fans => Color.FromArgb(20, 196, 196),
        _ => Color.FromArgb(130, 130, 135),
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.FormBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var px = Theme.Px(this);
        var bar = new RectangleF(0, 0, Width - 1, 12 * px);
        using (var path = Theme.RoundRect(bar, 6 * px))
        {
            using (var track = new SolidBrush(Theme.Track)) g.FillPath(track, path);
            if (total is { Total: > 0 } t)
            {
                var clip = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                float x = bar.Left;
                foreach (var item in t.Items.Where(i => i.Watts > 0.05))
                {
                    var w = (float)(item.Watts / t.Total * bar.Width);
                    using var b = new SolidBrush(PartColor(item.Part));
                    g.FillRectangle(b, x, bar.Top, w + 0.5f, bar.Height);
                    x += w;
                }
                g.Clip = clip;
            }
        }
        if (total is null) return;

        // Legend: coloured dot + "CPU 22 W" (estimates "~3.0 W"), wrapping onto a second row.
        using var font = Theme.UiFont(9.25f);
        float lx = 0, ly = bar.Bottom + 6 * px, dot = 8 * px, rowH = 17 * px;
        foreach (var item in total.Items)
        {
            var text = $"{item.Name} {(item.Estimated ? "~" : "")}{(item.Watts < 10 ? item.Watts.ToString("F1") : item.Watts.ToString("F0"))} W";
            var size = TextRenderer.MeasureText(g, text, font);
            var itemW = dot + 4 * px + size.Width + 10 * px;
            if (lx > 0 && lx + itemW > Width) { lx = 0; ly += rowH; }
            using (var b = new SolidBrush(PartColor(item.Part))) g.FillEllipse(b, lx, ly + (rowH - dot) / 2 - 1 * px, dot, dot);
            TextRenderer.DrawText(g, text, font, new Point((int)(lx + dot + 4 * px), (int)(ly + (rowH - size.Height) / 2 - 1 * px)),
                item.Estimated ? Color.FromArgb(190, 190, 196) : Theme.ForeMain); // estimates lighter, still readable
            lx += itemW;
        }
    }
}
