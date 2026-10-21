using System.Drawing.Drawing2D;

namespace WyvernCenter.UI;

/// <summary>Thin-track slider with a round thumb, like G-Helper's visual mode slider.</summary>
public class RSlider : Control
{
    int min, max = 100, value;
    bool dragging;

    public event EventHandler? ValueChanged;

    public Color Accent { get; set; } = Theme.Accent;
    public int SmallStep { get; set; } = 1;
    public int LargeStep { get; set; } = 10;

    public int Minimum { get => min; set { min = value; ClampRange(); } }
    public int Maximum { get => max; set { max = value; ClampRange(); } }

    void ClampRange()
    {
        if (max < min) max = min;
        value = Math.Clamp(value, min, max);
        Invalidate();
    }

    public int Value
    {
        get => value;
        set
        {
            int v = Math.Clamp(value, min, max);
            if (v == this.value) return;
            this.value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Set the value without raising <see cref="ValueChanged"/>.</summary>
    public void SetSilently(int v)
    {
        value = Math.Clamp(v, min, max);
        Invalidate();
    }

    public RSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    float Dpi => Theme.Px(this);
    float ThumbRadius => 8 * Dpi;
    float TrackLeft => ThumbRadius + 2;
    float TrackRight => Width - ThumbRadius - 2;

    float ValueToX(int v) => max == min ? TrackLeft : TrackLeft + (TrackRight - TrackLeft) * (v - min) / (float)(max - min);

    int XToValue(float x)
    {
        float t = Math.Clamp((x - TrackLeft) / (TrackRight - TrackLeft), 0, 1);
        int raw = min + (int)Math.Round(t * (max - min));
        return min + (int)Math.Round((raw - min) / (double)SmallStep) * SmallStep;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.FormBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float cy = Height / 2f, x = ValueToX(value), t = 4 * Dpi;

        using (var pen = new Pen(Theme.Track, t) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, TrackLeft, cy, TrackRight, cy);
        using (var pen = new Pen(Accent, t) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, TrackLeft, cy, x, cy);

        float r = ThumbRadius;
        using (var ring = new SolidBrush(Theme.ButtonMain))
            g.FillEllipse(ring, x - r, cy - r, r * 2, r * 2);
        float ri = r - 2.5f * Dpi;
        using (var fill = new SolidBrush(Focused ? Theme.Lighten(Accent, 25) : Accent))
            g.FillEllipse(fill, x - ri, cy - ri, ri * 2, ri * 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        dragging = true;
        Capture = true;
        Value = XToValue(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) Value = XToValue(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = false;
        Capture = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value += Math.Sign(e.Delta) * SmallStep;
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: Value -= SmallStep; break;
            case Keys.Right or Keys.Up: Value += SmallStep; break;
            case Keys.PageDown: Value -= LargeStep; break;
            case Keys.PageUp: Value += LargeStep; break;
            case Keys.Home: Value = min; break;
            case Keys.End: Value = max; break;
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
