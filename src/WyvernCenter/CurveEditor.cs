using WyvernCenter.Core.Features;
using System.Drawing.Drawing2D;
using WyvernCenter.UI;

namespace WyvernCenter;

/// <summary>
/// G-Helper-style fan curve: six points at the EC's fixed temperature thresholds (x), fan speed (y). Values are the
/// EC's 0–150 scale; everything is shown as percent of full speed. Drag a point up or down; points to its right are
/// raised so the curve never falls.
/// </summary>
public sealed class CurveEditor : Control
{
    public const int MaxSpeed = 150;
    private const int MaxTemp = 100;

    private int[] thresholds = { 0, 55, 64, 73, 76, 82 };
    private int[] speeds = new int[6];
    private int drag = -1;

    /// <summary>True while the user is dragging a point (hosts must not replace the curve meanwhile).</summary>
    public bool IsDragging => drag >= 0;

    public Color Accent { get; set; } = Theme.Accent;
    public string Title { get; set; } = "";
    public event EventHandler? CurveChanged;

    /// <summary>When true the curve is shown greyed out and can't be dragged (read-only, or the failsafe holds the fans).</summary>
    public bool Dimmed { get => dimmed; set { dimmed = value; Invalidate(); } }
    private bool dimmed;

    /// <summary>Text drawn over a dimmed curve.</summary>
    public string? OverlayText { get; set; }

    /// <summary>Live position: current temperature (°C) and fan value (0–150). Null hides the marker.</summary>
    public (int Temp, int Fan)? Live { get => live; set { live = value; Invalidate(); } }
    private (int Temp, int Fan)? live;

    public CurveEditor()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.ButtonSecond;
        ForeColor = Theme.ForeMain;
        Cursor = Cursors.Hand;
    }

    public int[] Speeds
    {
        get => (int[])speeds.Clone();
        set { speeds = value.Take(6).Select(v => Math.Clamp(v, 0, MaxSpeed)).ToArray(); Invalidate(); }
    }

    public int[] Thresholds
    {
        get => (int[])thresholds.Clone();
        set { thresholds = value.Take(6).ToArray(); Invalidate(); }
    }

    private Rectangle Plot
    {
        get
        {
            var px = Theme.Px(this);
            int l = (int)(44 * px), t = (int)(26 * px), r = (int)(24 * px), b = (int)(26 * px);
            return new Rectangle(l, t, Width - l - r, Height - t - b);
        }
    }

    private PointF PointAt(int i)
    {
        var p = Plot;
        var x = p.Left + p.Width * (thresholds[i] / (float)MaxTemp);
        var y = p.Bottom - p.Height * (speeds[i] / (float)MaxSpeed);
        return new PointF(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Plot;
        var px = Theme.Px(this);
        using var small = Theme.UiFont(8f);
        using var dim = new SolidBrush(Theme.ForeDim);
        using var grid = new Pen(Theme.BorderMain);

        using (var title = Theme.UiFont(9.5f, FontStyle.Bold))
        using (var fore = new SolidBrush(ForeColor))
            g.DrawString(Title, title, fore, p.Left, 4 * px);

        for (var pct = 0; pct <= 100; pct += 25)
        {
            var y = p.Bottom - p.Height * (pct / 100f);
            g.DrawLine(grid, p.Left, y, p.Right, y);
            g.DrawString($"{pct}%", small, dim, 4 * px, y - 8 * px);
        }
        for (var t = 0; t <= MaxTemp; t += 20)
        {
            var x = p.Left + p.Width * (t / (float)MaxTemp);
            g.DrawLine(grid, x, p.Top, x, p.Bottom);
            g.DrawString($"{t}°", small, dim, x - 8 * px, p.Bottom + 4 * px);
        }

        var pts = Enumerable.Range(0, 6).Select(PointAt).ToArray();
        var accent = dimmed ? Theme.Lighten(Theme.Track, 20) : Accent;
        using var line = new Pen(accent, 2.5f * px);
        g.DrawLines(line, pts);
        using var fill = new SolidBrush(accent);
        using var fore2 = new SolidBrush(ForeColor);
        for (var i = 0; i < pts.Length; i++)
        {
            var r = (i == drag ? 7 : 5) * px;
            g.FillEllipse(fill, pts[i].X - r, pts[i].Y - r, 2 * r, 2 * r);
            if (!dimmed) g.DrawString($"{FanPercent.Of(speeds[i])}", small, fore2, pts[i].X - 8 * px, pts[i].Y - 20 * px);
        }

        if (live is { } lv && lv.Temp > 0)
        {
            var x = p.Left + p.Width * Math.Clamp(lv.Temp / (float)MaxTemp, 0, 1);
            var y = p.Bottom - p.Height * Math.Clamp(lv.Fan / (float)MaxSpeed, 0, 1);
            using var guide = new Pen(Color.FromArgb(120, Theme.ForeMain), 1f * px) { DashStyle = DashStyle.Dash };
            g.DrawLine(guide, x, p.Top, x, p.Bottom);
            using var dot = new SolidBrush(Theme.ForeMain);
            g.FillEllipse(dot, x - 5 * px, y - 5 * px, 10 * px, 10 * px);
            var label = $"now {lv.Temp}°C · {FanPercent.Of(lv.Fan)}%";
            g.DrawString(label, small, dot, Math.Min(x + 6 * px, p.Right - g.MeasureString(label, small).Width), p.Top + 2 * px);
        }

        if (dimmed && !string.IsNullOrEmpty(OverlayText))
        {
            using var overlayFont = Theme.UiFont(9f, FontStyle.Bold);
            var size = g.MeasureString(OverlayText, overlayFont, (int)(p.Width - 20 * px));
            var box = new RectangleF(p.Left + (p.Width - size.Width) / 2 - 8 * px, p.Top + (p.Height - size.Height) / 2 - 6 * px, size.Width + 16 * px, size.Height + 12 * px);
            using var back = new SolidBrush(Color.FromArgb(220, Theme.ButtonMain));
            g.FillRectangle(back, box);
            g.DrawString(OverlayText, overlayFont, fore2, new RectangleF(box.X + 8 * px, box.Y + 6 * px, size.Width + 2, size.Height + 2));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (dimmed) return;
        var best = -1;
        var bestDist = float.MaxValue;
        for (var i = 0; i < 6; i++)
        {
            var pt = PointAt(i);
            var d = Math.Abs(pt.X - e.X) + Math.Abs(pt.Y - e.Y) * 0.25f;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        drag = best;
        MoveTo(e.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag >= 0 && e.Button == MouseButtons.Left) MoveTo(e.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var wasDragging = drag >= 0;
        drag = -1; // always end the drag, even if the editor was dimmed meanwhile (failsafe)
        if (dimmed || !wasDragging) { Invalidate(); return; }
        Invalidate();
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoveTo(int y)
    {
        if (drag < 0) return;
        var p = Plot;
        var v = (int)Math.Round((p.Bottom - y) / (float)p.Height * MaxSpeed);
        v = Math.Clamp(v, 0, MaxSpeed);
        speeds[drag] = v;
        // Keep the curve non-decreasing: drag neighbours along.
        for (var i = drag + 1; i < 6; i++) speeds[i] = Math.Max(speeds[i], v);
        for (var i = drag - 1; i >= 0; i--) speeds[i] = Math.Min(speeds[i], v);
        Invalidate();
    }
}
