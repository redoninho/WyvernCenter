namespace WyvernCenter.UI;

/// <summary>
/// Top-to-bottom layout builder in 96-DPI units, scaled by the user UI scale.
/// WinForms DPI autoscaling is applied on top by the host form.
/// </summary>
public sealed class UiLayout
{
    readonly Control host;

    public int Width { get; }
    public int Pad { get; }
    public int Gap { get; }
    public int Y { get; set; }

    public UiLayout(Control host, int width)
    {
        this.host = host;
        Width = S(width);
        Pad = S(16);
        Gap = S(8);
    }

    public int S(int value) => Theme.S(value);

    private ToolTip? tips;

    /// <summary>One tooltip per window, quick to appear: the info icons show their text on hover, no click needed.</summary>
    public ToolTip Tips
    {
        get
        {
            if (tips is null)
            {
                tips = new ToolTip { InitialDelay = 80, ReshowDelay = 40, AutoPopDelay = 30000, ShowAlways = true };
                host.Disposed += (_, _) => tips.Dispose();
            }
            return tips;
        }
    }

    /// <summary>
    /// A small ⓘ right after a header's title; hovering it shows <paramref name="text"/>. Returns the icon so its text
    /// can be changed later (<see cref="SetInfo"/>).
    /// </summary>
    public Label Info(Label title, string text)
    {
        var w = TextRenderer.MeasureText(title.Text, title.Font).Width;
        var icon = new Label
        {
            Text = Glyphs.Info,
            Font = Theme.IconFont(10f),
            ForeColor = Theme.ForeDim,
            Bounds = new Rectangle(title.Left + w + S(2), title.Top, S(22), title.Height),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Help,
        };
        title.Width = Math.Min(title.Width, w + S(2));
        host.Controls.Add(icon);
        icon.BringToFront();
        Tips.SetToolTip(icon, TextWrap.Lines(text));
        icon.MouseEnter += (_, _) => icon.ForeColor = Theme.ForeMain;
        icon.MouseLeave += (_, _) => icon.ForeColor = Theme.ForeDim;
        return icon;
    }

    public void SetInfo(Label icon, string text) => Tips.SetToolTip(icon, TextWrap.Lines(text));

    /// <summary>A hover tip on any control, wrapped like the info icons.</summary>
    public void Tip(Control c, string text) => Tips.SetToolTip(c, TextWrap.Lines(text));

    /// <summary>A header with an ⓘ that explains it on hover.</summary>
    public (Label Title, Label Right, Label Info) Header(string glyph, string text, string info)
    {
        var (title, right) = Header(glyph, text);
        return (title, right, Info(title, info));
    }

    public void Space(int value) => Y += S(value);

    public (Label Title, Label Right) Header(string glyph, string text)
    {
        int h = S(28);
        var icon = new Label
        {
            Text = glyph,
            Font = Theme.IconFont(13f),
            Bounds = new Rectangle(Pad - S(2), Y, S(28), h),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var title = new Label
        {
            Text = text,
            Font = Theme.UiFont(10.5f, FontStyle.Bold),
            Bounds = new Rectangle(Pad + S(26), Y, Width / 2 + S(20), h),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseMnemonic = false,
        };
        var right = new Label
        {
            Bounds = new Rectangle(Width / 2, Y, Width / 2 - Pad, h),
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
        };
        host.Controls.AddRange(new Control[] { icon, right, title });

        // Keep the right-hand text fully visible: size it to its text and give the title the rest.
        void Fit()
        {
            int edge = right.Right;
            int w = string.IsNullOrEmpty(right.Text) ? 0 : TextRenderer.MeasureText(right.Text, right.Font).Width + S(4);
            right.SetBounds(edge - w, right.Top, w, right.Height);
            title.Width = Math.Max(S(40), right.Left - title.Left - S(6));
        }
        right.TextChanged += (_, _) => Fit();
        Fit();
        right.Resize += (_, _) => { if (right.Width > 0 && right.Text.Length > 0) title.Width = Math.Max(S(40), right.Left - title.Left - S(6)); };
        Y += S(31);
        return (title, right);
    }

    public List<T> Row<T>(int count, int height, Func<int, T> make) where T : Control
    {
        int h = S(height);
        int w = (Width - 2 * Pad - (count - 1) * Gap) / count;
        var list = new List<T>();
        for (int i = 0; i < count; i++)
        {
            var c = make(i);
            int x = Pad + i * (w + Gap);
            c.Bounds = new Rectangle(x, Y, i == count - 1 ? Width - Pad - x : w, h);
            host.Controls.Add(c);
            list.Add(c);
        }
        Y += h + Gap;
        return list;
    }

    public RSlider Slider(int min, int max, Color accent, int step)
    {
        var s = new RSlider
        {
            Minimum = min,
            Maximum = max,
            Accent = accent,
            SmallStep = step,
            LargeStep = step * 10,
            Bounds = new Rectangle(Pad, Y, Width - 2 * Pad, S(26)),
        };
        host.Controls.Add(s);
        Y += S(26) + Gap;
        return s;
    }

    /// <summary>Full-width control.</summary>
    public T Add<T>(T control, int height) where T : Control
    {
        control.Bounds = new Rectangle(Pad, Y, Width - 2 * Pad, S(height));
        host.Controls.Add(control);
        Y += S(height) + Gap;
        return control;
    }

    /// <summary>An icon and a short line of text in one colour (e.g. a red warning under a header).</summary>
    public (Label Icon, Label Text) Notice(string glyph, string text, Color color, int height = 20)
    {
        int h = S(height);
        var icon = new Label
        {
            Text = glyph,
            Font = Theme.IconFont(10f),
            ForeColor = color,
            Bounds = new Rectangle(Pad, Y, S(20), h),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var label = new Label
        {
            Text = text,
            Font = Theme.UiFont(9f, FontStyle.Bold),
            ForeColor = color,
            Bounds = new Rectangle(Pad + S(20), Y, Width - 2 * Pad - S(20), h),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseMnemonic = false,
        };
        host.Controls.AddRange(new Control[] { icon, label });
        Y += h + Gap;
        return (icon, label);
    }

    public Label Text(string text, Color color, float size = 9.5f, int height = 22)
    {
        return Add(new Label
        {
            Text = text,
            ForeColor = color,
            Font = Theme.UiFont(size),
            TextAlign = ContentAlignment.MiddleLeft,
        }, height);
    }
}
