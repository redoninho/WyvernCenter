using WyvernCenter.Core.Profile;
using WyvernCenter.UI;

namespace WyvernCenter;

/// <summary>App settings that aren't about the laptop: UI scale, hotkeys, the in-game overlay, start with Windows.</summary>
public sealed class SettingsForm : Form
{
    private static readonly int[] Scales = { 70, 75, 80, 85, 90, 100, 110, 125 };
    private static readonly string[] Corners = { "Top left", "Top right", "Bottom left", "Bottom right" };

    private readonly AppController app;
    private readonly Label lblHotkey, lblHotkeyStatus;
    private readonly RButton btnHotkey;
    private readonly CheckBox chkStartup, chkOverlay;
    private readonly Label lblReport;
    private readonly Label lblOverlayHotkey, lblOverlayStatus, lblOpacity;
    private readonly RButton btnOverlayHotkey;
    private bool capturing, captureOverlay, syncing;
    // The last capture result per hotkey, redrawn by Sync so a refusal message stays visible.
    private (string Text, Color Color)? profileMessage, overlayMessage;

    public SettingsForm(AppController app)
    {
        this.app = app;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.FormBack;
        ForeColor = Theme.ForeMain;
        Font = Theme.UiFont(10.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Settings";
        Icon = app.AppIcon;

        var L = new UiLayout(this, 400);
        L.Space(12);

        L.Header(Glyphs.Screen, "UI scale", "Makes every Wyvern Center window bigger or smaller.");
        var ddScale = L.Add(new RDropdown { Prefix = "UI scale: " }, 32);
        ddScale.Items.AddRange(Scales.Select(s => $"{s}%"));
        var current = Array.IndexOf(Scales, app.Settings.UiScale);
        ddScale.SetSilently(current >= 0 ? current : Array.IndexOf(Scales, 100));
        // Changing the scale rebuilds this window too: do it after the dropdown's click has finished.
        ddScale.SelectedIndexChanged += (_, _) => { var v = Scales[ddScale.SelectedIndex]; app.Post(() => app.SetUiScale(v)); };
        L.Space(4);

        (_, lblHotkey, _) = L.Header(Glyphs.Keyboard, "Profile hotkey",
            "Switches to the next profile ticked \"In hotkey cycle\" in Edit profiles. A popup shows which one.");
        lblHotkey.ForeColor = Theme.ForeMain;
        btnHotkey = L.Add(new RButton { Text = "Change…", BorderColor = Theme.Accent }, 32);
        btnHotkey.Click += (_, _) => StartCapture(overlay: false);
        lblHotkeyStatus = L.Text("", Theme.ForeDim, 8.75f, 34);
        lblHotkeyStatus.UseMnemonic = false;
        L.Space(4);

        (_, lblOverlayHotkey, _) = L.Header(Glyphs.Layers, "In-game overlay",
            "FPS (with 1% low and frame time) and a graph of the last minute, CPU, GPU, power draw and system readings, in a corner of the screen. " +
            "Clicks pass through it. It shows over windowed and borderless games; true exclusive fullscreen hides it, so set the game to borderless. " +
            "FPS works for DirectX 9–12 games.");
        lblOverlayHotkey.ForeColor = Theme.ForeMain;
        chkOverlay = L.Add(new CheckBox { Text = "Show overlay", ForeColor = Theme.ForeMain }, 26);
        chkOverlay.CheckedChanged += (_, _) => { if (!syncing) app.ShowOverlay(chkOverlay.Checked); };
        lblOpacity = L.Text("", Theme.ForeMain, 9f, 20);
        var sliderOpacity = L.Slider(0, 90, Theme.Accent, 5);
        sliderOpacity.SetSilently(app.Settings.OverlayOpacity);
        ShowOpacity(app.Settings.OverlayOpacity);
        sliderOpacity.ValueChanged += (_, _) =>
        {
            ShowOpacity(sliderOpacity.Value);
            app.Settings.OverlayOpacity = sliderOpacity.Value;
            app.OverlaySettingsChanged();
        };
        var ddCorner = L.Add(new RDropdown { Prefix = "Corner: " }, 30);
        ddCorner.Items.AddRange(Corners);
        ddCorner.SetSilently(Math.Clamp(app.Settings.OverlayCorner, 0, 3));
        ddCorner.SelectedIndexChanged += (_, _) => { app.Settings.OverlayCorner = ddCorner.SelectedIndex; app.OverlaySettingsChanged(); };
        // Which lines it shows: two rows of three.
        var lines = new (string Name, Func<bool> Get, Action<bool> Set)[]
        {
            ("FPS", () => app.Settings.OverlayFps, v => app.Settings.OverlayFps = v),
            ("FPS graph", () => app.Settings.OverlayGraph, v => app.Settings.OverlayGraph = v),
            ("CPU", () => app.Settings.OverlayCpu, v => app.Settings.OverlayCpu = v),
            ("GPU", () => app.Settings.OverlayGpu, v => app.Settings.OverlayGpu = v),
            ("Power", () => app.Settings.OverlayPower, v => app.Settings.OverlayPower = v),
            ("System", () => app.Settings.OverlaySys, v => app.Settings.OverlaySys = v),
        };
        for (var row = 0; row < 2; row++)
        {
            var first = row * 3;
            L.Row(3, 24, i =>
            {
                var line = lines[first + i];
                var c = new CheckBox { Text = line.Name, ForeColor = Theme.ForeMain, Checked = line.Get() };
                c.CheckedChanged += (_, _) => { line.Set(c.Checked); app.OverlaySettingsChanged(); };
                return c;
            });
        }
        btnOverlayHotkey = L.Add(new RButton { Text = "Change hotkey…", BorderColor = Theme.Accent }, 30);
        btnOverlayHotkey.Click += (_, _) => StartCapture(overlay: true);
        lblOverlayStatus = L.Text("", Theme.ForeDim, 8.75f, 22);
        lblOverlayStatus.UseMnemonic = false;
        L.Space(4);

        L.Header(Glyphs.Power, "Startup");
        chkStartup = L.Add(new CheckBox { Text = "Run Wyvern Center when Windows starts", ForeColor = Theme.ForeMain, Checked = StartupTask.IsEnabled() }, 28);
        chkStartup.CheckedChanged += (_, _) =>
        {
            if (syncing) return;
            var error = StartupTask.Set(chkStartup.Checked);
            if (error is null) return;
            syncing = true; // revert without re-entering this handler
            chkStartup.Checked = !chkStartup.Checked;
            syncing = false;
            MessageBox.Show(this, error, "Run on startup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        L.Space(4);

        // ---- This laptop: support level, the experimental opt-in, and a report for adding the model ----------------
        var (_, lblSupport, _) = L.Header(Glyphs.Shield, "This laptop", app.Support switch
        {
            _ when app.Interface == FirmwareInterface.Wmi2 =>
                "Newer MSI laptop (WMI v2, 11th gen or later). This version reads it (temperatures, fans, modes) but changes nothing yet. " +
                "A support report from this laptop helps turn on changes for it.",
            ModelSupport.Supported => "Supported: this exact firmware has a checked profile.",
            ModelSupport.SameBoard => "Known board, but this firmware version hasn't been checked yet. Its settings are very likely the same; changes need the experimental option below.",
            ModelSupport.Generic => "No profile for this model yet. Readings work. Experimental changes cover performance mode, fan mode and keyboard backlight (checked against the current values first), not fan curves.",
            _ => "No MSI firmware interface found: Wyvern Center can only show readings here.",
        });
        lblSupport.Text = app.Support switch
        {
            _ when app.Interface == FirmwareInterface.Wmi2 => "Read-only beta",
            ModelSupport.Supported => "Supported",
            ModelSupport.SameBoard => "Firmware not confirmed",
            ModelSupport.Generic => "No profile yet",
            _ => "Not supported",
        };
        lblSupport.ForeColor = app.Support == ModelSupport.Supported ? Theme.ForeDim : Theme.Warn;
        L.Text($"{app.ModelName} · EC firmware {(app.FirmwareId == "" ? "unknown" : app.FirmwareId)}", Theme.ForeMain, 8.75f, 22).UseMnemonic = false;
        if (app.Interface == FirmwareInterface.Scm && app.Support is ModelSupport.SameBoard or ModelSupport.Generic)
        {
            var chkExperimental = L.Add(new CheckBox
            {
                Text = "Allow changes on this laptop (experimental)",
                ForeColor = Theme.Warn,
                Checked = app.Settings.ExperimentalFirmware == app.FirmwareId && app.FirmwareId != "",
                Enabled = app.IsAdmin && SecureStorage.Ready && app.FirmwareId != "",
            }, 28);
            chkExperimental.CheckedChanged += (_, _) =>
            {
                if (chkExperimental.Checked && MessageBox.Show(this,
                        "Wyvern Center will write to this laptop's firmware without a profile checked on this model. Original values are " +
                        "saved first (Restore puts them back) and the thermal failsafe stays on, but a wrong value could make fans or " +
                        "performance modes misbehave until restored.\n\nAllow changes on this laptop?",
                        "Experimental support", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    chkExperimental.Checked = false;
                    return;
                }
                if (!chkExperimental.Checked && app.CanRestore && !app.Ec.ReadOnlyMode)
                    MessageBox.Show(this, "Wyvern Center will now put this laptop's original settings back, then stop making changes after the restart.",
                        "Experimental support", MessageBoxButtons.OK, MessageBoxIcon.Information);
                app.SetExperimental(chkExperimental.Checked);
            };
        }
        var btnVerify = L.Add(new RButton { Text = "Verify my laptop…", BorderColor = Theme.Accent, Enabled = app.CanVerify }, 30);
        L.Tips.SetToolTip(btnVerify, TextWrap.Lines(app.CanVerify
            ? "A few minutes of guided checks: Wyvern shows what it reads, you press a few keys, and it tries each setting briefly (always putting it back). Your answers go into the support report."
            : "Needs Wyvern Center running as administrator on an MSI laptop."));
        btnVerify.Click += (_, _) => app.ShowVerify();
        var btnReport = L.Add(new RButton { Text = "Create support report…", BorderColor = Theme.Accent }, 30);
        L.Tips.SetToolTip(btnReport, TextWrap.Lines("Saves this laptop's firmware details and opens the folder, to attach to a \"Support my laptop\" request."));
        lblReport = L.Text("", Theme.ForeDim, 8.25f, 34);
        lblReport.UseMnemonic = false;
        btnReport.Click += (_, _) =>
        {
            btnReport.Enabled = false;
            lblReport.Text = "Reading the firmware…";
            app.CreateSupportReport((folder, error) =>
            {
                if (IsDisposed) return;
                btnReport.Enabled = true;
                lblReport.ForeColor = error is null ? Theme.ForeDim : Theme.Warn;
                lblReport.Text = error is null ? $"Saved: {folder}" : "Couldn't create the report: " + error;
            });
        };
        L.Space(8);

        ClientSize = new Size(L.Width, L.Y);
        ResumeLayout(false);
        app.StateChanged += Sync;
        // Clicking away while recording would otherwise leave the hotkey switched off.
        Deactivate += (_, _) => { if (capturing) { EndCapture(); SayTarget("Unchanged.", Theme.ForeDim); } };
        Sync();
    }

    private void ShowOpacity(int pct) => lblOpacity.Text = pct == 0 ? "Background: none (text only)" : $"Background: {pct}% opaque";

    private void Sync()
    {
        if (IsDisposed || capturing) return;
        syncing = true;
        chkOverlay.Checked = app.OverlayShown;
        syncing = false;
        lblOverlayHotkey.Text = app.OverlayHotkeyText;
        if (app.OverlayHotkeyError is { } overlayError) ShowOverlayStatus(overlayError, Theme.Warn);
        else ShowOverlayStatus(overlayMessage?.Text ?? "", overlayMessage?.Color ?? Theme.ForeDim);
        lblHotkey.Text = app.HotkeyText;
        if (app.HotkeyError is { } error) ShowProfileStatus(error, Theme.Warn);
        else if (profileMessage is { } m) ShowProfileStatus(m.Text, m.Color);
        else if (app.Store.NextInCycle(null) is null) ShowProfileStatus("No profiles are in the hotkey cycle: tick \"In hotkey cycle\" in Edit profiles.", Theme.Warn);
        else ShowProfileStatus("Cycles: " + string.Join(" → ", app.Store.Profiles.Where(p => p.IsInCycle).Select(p => p.Name)), Theme.ForeDim);
    }

    private RButton CaptureButton => captureOverlay ? btnOverlayHotkey : btnHotkey;

    private void StartCapture(bool overlay)
    {
        if (capturing) EndCapture();
        captureOverlay = overlay;
        capturing = true;
        app.PauseHotkey(true); // otherwise pressing the current combination would trigger it instead
        CaptureButton.Text = "Press the new keys… (Esc cancels)";
        CaptureButton.Activated = true;
        SayTarget("Hold Ctrl and/or Alt, then press a key.", Theme.ForeDim);
    }

    private void EndCapture()
    {
        capturing = false;
        app.PauseHotkey(false);
        btnHotkey.Text = "Change…";
        btnOverlayHotkey.Text = "Change hotkey…";
        btnHotkey.Activated = btnOverlayHotkey.Activated = false;
        Sync();
    }

    private void SayTarget(string text, Color color)
    {
        if (captureOverlay) SayOverlay(text, color);
        else Say(text, color);
    }

    private void SayOverlay(string text, Color color)
    {
        overlayMessage = (text, color);
        ShowOverlayStatus(text, color);
    }

    private void ShowOverlayStatus(string text, Color color)
    {
        lblOverlayStatus.Text = text;
        lblOverlayStatus.ForeColor = color;
    }

    private void ShowProfileStatus(string text, Color color)
    {
        lblHotkeyStatus.Text = text;
        lblHotkeyStatus.ForeColor = color;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!capturing) return base.ProcessCmdKey(ref msg, keyData);
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape)
        {
            EndCapture();
            SayTarget("Unchanged.", Theme.ForeDim);
            return true;
        }
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return true; // wait for the real key
        uint mods = 0;
        if (keyData.HasFlag(Keys.Control)) mods |= HotkeyWindow.ModControl;
        if (keyData.HasFlag(Keys.Alt)) mods |= HotkeyWindow.ModAlt;
        if (keyData.HasFlag(Keys.Shift)) mods |= HotkeyWindow.ModShift;
        capturing = false;
        var overlay = captureOverlay;
        var error = app.SetHotkey(overlay, mods, (int)key);
        EndCapture();
        if (error is null) SayTarget("Saved: " + (overlay ? app.OverlayHotkeyText : app.HotkeyText), Theme.ForeMain);
        else SayTarget(error, Theme.Warn);
        return true;
    }

    private void Say(string text, Color color)
    {
        profileMessage = (text, color);
        ShowProfileStatus(text, color);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (capturing) EndCapture();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) app.StateChanged -= Sync;
        base.Dispose(disposing);
    }
}
