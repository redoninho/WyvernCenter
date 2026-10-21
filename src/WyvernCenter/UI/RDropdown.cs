using System.Drawing.Drawing2D;

namespace WyvernCenter.UI;

/// <summary>Dark dropdown box (G-Helper's combo look) that opens a themed menu.</summary>
public class RDropdown : Control
{
    int selected;
    bool hover;

    public List<string> Items { get; } = new();
    public string Prefix { get; set; } = "";

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => selected;
        set
        {
            if (value == selected || value < 0 || value >= Items.Count) return;
            selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetSilently(int index)
    {
        selected = Math.Clamp(index, 0, Math.Max(0, Items.Count - 1));
        Invalidate();
    }

    public RDropdown()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Theme.UiFont(10.5f);
        ForeColor = Theme.ForeMain;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = Theme.Px(this);
        g.Clear(Parent?.BackColor ?? Theme.FormBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color back = hover ? Theme.Lighten(Theme.ButtonMain, 10) : Theme.ButtonMain;
        using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 4 * s))
        using (var brush = new SolidBrush(back))
            g.FillPath(brush, path);

        string text = Items.Count > 0 ? Prefix + Items[selected] : Prefix;
        int pad = (int)(9 * s), chevron = (int)(18 * s);
        TextRenderer.DrawText(g, text, Font, new Rectangle(pad, 0, Width - pad - chevron - pad, Height), Enabled ? ForeColor : Theme.ForeDim, back,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        using var icon = Theme.IconFont(7.5f);
        TextRenderer.DrawText(g, Glyphs.ChevronDown, icon, new Rectangle(Width - chevron - pad / 2, 0, chevron, Height), Theme.ForeDim, back,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true, Font = Font };
        for (int i = 0; i < Items.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(Items[i]) { Checked = i == selected };
            item.Click += (_, _) => SelectedIndex = index;
            menu.Items.Add(item);
        }
        menu.MinimumSize = new Size(Width, 0);
        menu.Closed += (_, _) => { if (IsHandleCreated && !IsDisposed) BeginInvoke(menu.Dispose); else menu.Dispose(); };
        menu.Show(this, new Point(0, Height + 2));
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Down) SelectedIndex = selected + 1;
        else if (e.KeyCode == Keys.Up) SelectedIndex = selected - 1;
        else if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
    }
}

/// <summary>Dark renderer for dropdown and tray menus.</summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.ForeMain : Theme.ForeDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.ForeMain;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        using var icon = Theme.IconFont(9f);
        var r = e.ImageRectangle;
        r.Inflate(4, 4);
        TextRenderer.DrawText(e.Graphics, Glyphs.Check, icon, r, Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(40, 40, 40);
        static readonly Color Hover = Color.FromArgb(62, 62, 62);

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Theme.BorderMain;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Theme.BorderMain;
        public override Color SeparatorLight => Theme.BorderMain;
        public override Color CheckBackground => Back;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
        public override Color ButtonSelectedBorder => Hover;
    }
}
