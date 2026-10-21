namespace WyvernCenter.UI;

/// <summary>Small dark dialog asking for one line of text (used to rename a profile).</summary>
public static class TextPrompt
{
    /// <returns>The trimmed text, or null when cancelled.</returns>
    public static string? Ask(IWin32Window owner, string title, string label, string initial)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = Theme.FormBack,
            ForeColor = Theme.ForeMain,
            Font = Theme.UiFont(10.5f),
            ClientSize = new Size(Theme.S(360), Theme.S(132)),
        };
        int pad = Theme.S(16), w = form.ClientSize.Width - 2 * pad;
        var caption = new Label { Text = label, Bounds = new Rectangle(pad, Theme.S(12), w, Theme.S(22)) };
        var box = new TextBox
        {
            Text = initial,
            Bounds = new Rectangle(pad, Theme.S(38), w, Theme.S(28)),
            BackColor = Theme.ButtonMain,
            ForeColor = Theme.ForeMain,
            BorderStyle = BorderStyle.FixedSingle,
            MaxLength = 40,
        };
        int bw = Theme.S(90), bh = Theme.S(32), top = Theme.S(84);
        var cancel = new RButton { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(form.ClientSize.Width - pad - bw, top, bw, bh) };
        var ok = new RButton { Text = "OK", DialogResult = DialogResult.OK, BorderColor = Theme.Accent, Activated = true, Bounds = new Rectangle(cancel.Left - Theme.S(8) - bw, top, bw, bh) };
        form.Controls.AddRange(new Control[] { caption, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.HandleCreated += (_, _) => Theme.DarkTitleBar(form.Handle);
        form.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}
