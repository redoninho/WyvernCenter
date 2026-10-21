using WyvernCenter.Core.Features;
using WyvernCenter.Core.Profile;
using WyvernCenter.UI;

namespace WyvernCenter;

public sealed class MainForm : Form
{
    private static readonly int[] ChargePresets = { 60, 80, 100 };
    private static readonly string[] BacklightNames = { "Off", "Low", "Mid", "High" };

    private readonly AppController app;
    private readonly Dictionary<string, RButton> profileButtons = new();
    private readonly string[] profileIds;
    private readonly Label lblProfile, lblProfileRight, lblCardCpu, lblCooling, lblCoolingRight, lblBattery, lblBatteryRight;
    private readonly Label lblChargeOffIcon, lblChargeOff;
    private readonly Label lblScreen, lblKeyboard, lblStatus;
    private readonly RButton btnCoolerBoost, btnEdit, btnOverlay;
    private readonly RButton? btnNew;
    private bool viewing;
    private readonly List<RButton> chargeButtons, backlightButtons, refreshButtons;
    private readonly RSlider sliderCharge;
    private readonly CheckBox chkAuto;
    private readonly RDropdown ddAc, ddDc;
    private readonly Label lblMsiApps;
    private readonly ToolTip tips = new() { InitialDelay = 300 };
    private readonly System.Windows.Forms.Timer chargeDebounce = new() { Interval = 450 };
    private RButton? btnRestore;
    private bool syncing;

    public static string GlyphFor(Profile p) => p.Id switch
    {
        Presets.Battery => Glyphs.Battery,
        Presets.Quiet => Glyphs.Moon,
        Presets.Balanced => Glyphs.Power,
        Presets.Performance => Glyphs.Bolt,
        Presets.Extreme => Glyphs.Star,
        _ => Glyphs.Brush,
    };

    public MainForm(AppController app)
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
        StartPosition = FormStartPosition.Manual;
        Text = "Wyvern Center — " + app.ModelName;
        Icon = app.AppIcon;

        var L = new UiLayout(this, 520);
        L.Space(12);

        // ---- Profiles: one tile per profile, then Edit profiles ------------------------------------------
        (lblProfile, lblProfileRight) = L.Header(Glyphs.Gear, "Profile");
        lblProfileRight.ForeColor = Theme.ForeDim;
        var profiles = app.Store.Profiles;
        profileIds = profiles.Select(p => p.Id).ToArray();
        // Tiles: every profile, "+ New" while there's room for one of your own, then Edit profiles (two rows at most).
        var canAdd = app.Store.CanAddOwn;
        var tiles = profiles.Count + (canAdd ? 1 : 0) + 1;
        RButton? edit = null, add = null;
        for (var start = 0; start < tiles; start += 4)
        {
            var first = start;
            L.Row(4, 64, i =>
            {
                var index = first + i;
                if (canAdd && index == profiles.Count)
                {
                    add = new RButton { Text = "New profile", Glyph = Glyphs.Add, BorderColor = Theme.Accent };
                    add.Click += (_, _) => app.AddProfileFromMain();
                    tips.SetToolTip(add, TextWrap.Lines($"Make a profile of your own (a copy of the one in use), up to {Presets.MaxOwnProfiles}"));
                    return add;
                }
                if (index == profiles.Count + (canAdd ? 1 : 0))
                {
                    edit = new RButton { Text = "Edit profiles", Glyph = Glyphs.Gear, BorderColor = Theme.Accent };
                    edit.Click += (_, _) => app.ShowProfiles();
                    tips.SetToolTip(edit, TextWrap.Lines("Change what each profile does: CPU, GPU and fan curves"));
                    return edit;
                }
                if (index >= tiles) return new RButton { Visible = false };
                var p = profiles[index];
                var id = p.Id;
                var b = new RButton { Text = p.Name, Glyph = GlyphFor(p), BorderColor = AppController.ProfileColor(p), GlyphColor = AppController.ProfileColor(p) };
                b.Click += (_, _) => app.UseProfile(id);
                profileButtons[id] = b;
                return b;
            });
        }
        btnEdit = edit!;
        btnNew = add;

        // What the profile in use does (CPU and GPU caps). Live CPU readings are in the Monitor window.
        lblCardCpu = L.Text("", Theme.ForeMain, 9f, 19);
        lblCardCpu.UseMnemonic = false;
        lblCardCpu.AutoEllipsis = true;
        L.Space(4);

        // ---- Cooling ---------------------------------------------------------------------------------
        (lblCooling, lblCoolingRight) = L.Header(Glyphs.Thermometer, "Cooling");
        lblCoolingRight.ForeColor = Theme.ForeDim;
        var coolRow = L.Row(3, 34, i => new RButton { BorderColor = Theme.Accent });
        btnCoolerBoost = coolRow[0];
        btnCoolerBoost.Text = "Max Cooling";
        btnCoolerBoost.Glyph = Glyphs.Bolt;
        btnCoolerBoost.Click += (_, _) => app.SetMaxCooling(!app.MaxCoolingOn);
        var btnMonitor = coolRow[1];
        btnMonitor.Text = "Monitor";
        btnMonitor.Glyph = Glyphs.Screen;
        btnMonitor.Click += (_, _) => app.ShowMonitor();
        tips.SetToolTip(btnMonitor, TextWrap.Lines("Live graphs: temperatures, fans, power, clocks"));
        btnOverlay = coolRow[2];
        btnOverlay.Text = "Overlay";
        btnOverlay.Glyph = Glyphs.Layers;
        btnOverlay.Click += (_, _) => app.ToggleOverlay();
        L.Space(4);

        // ---- Screen ---------------------------------------------------------------------------
        (lblScreen, _) = L.Header(Glyphs.Screen, "Screen");
        var rates = app.RefreshRates;
        refreshButtons = L.Row(Math.Max(1, rates.Length + 1), 34, i =>
        {
            var b = new RButton { BorderColor = Theme.Accent };
            if (i == 0) { b.Text = "Auto"; b.Click += (_, _) => app.SetRefreshAuto(); }
            else
            {
                var hz = rates[i - 1];
                b.Text = $"{hz}Hz";
                b.BorderColor = Theme.Accent;
                b.Click += (_, _) => app.SetRefresh(hz);
            }
            return b;
        });
        tips.SetToolTip(refreshButtons[0], TextWrap.Lines("Auto: highest rate on the charger, lowest on battery"));
        L.Space(4);

        // ---- Keyboard ------------------------------------------------------------------------
        (lblKeyboard, _) = L.Header(Glyphs.Keyboard, "Keyboard");
        backlightButtons = L.Row(4, 30, i =>
        {
            var b = new RButton { Text = BacklightNames[i], BorderColor = Theme.Accent };
            b.Click += (_, _) => app.SetSetting("kbdBacklight", i, "Backlight");
            return b;
        });
        L.Space(4);

        // ---- Battery ----------------------------------------------------------------------
        (lblBattery, lblBatteryRight) = L.Header(Glyphs.Battery, "Battery Charge Limit");
        lblBatteryRight.ForeColor = Theme.ForeDim;
        (lblChargeOffIcon, lblChargeOff) = L.Notice(Glyphs.Warning, "Disabled", Theme.Accent);
        sliderCharge = L.Slider(50, 100, Theme.Accent, 5);
        // Sliders apply once the user stops moving them: one write, the final value.
        sliderCharge.ValueChanged += (_, _) =>
        {
            lblBattery.Text = $"Battery Charge Limit: {sliderCharge.Value}%";
            if (!syncing) { chargeDebounce.Stop(); chargeDebounce.Start(); }
        };
        chargeDebounce.Tick += (_, _) => { chargeDebounce.Stop(); app.SetSetting("chargeLimit", sliderCharge.Value, "Charge limit"); };
        chargeButtons = L.Row(ChargePresets.Length, 30, i =>
        {
            var b = new RButton { Text = $"{ChargePresets[i]}%", BorderColor = Theme.Accent };
            b.Click += (_, _) => app.SetSetting("chargeLimit", ChargePresets[i], "Charge limit");
            return b;
        });
        tips.SetToolTip(chargeButtons[1], TextWrap.Lines("80% keeps the battery healthiest if you're mostly plugged in"));
        L.Space(4);

        // ---- Auto switch ---------------------------------------------------------------------
        L.Header(Glyphs.Refresh, "Profile on charger / on battery");
        chkAuto = new CheckBox { Text = "Enabled", ForeColor = Theme.ForeMain };
        ddAc = new RDropdown { Prefix = "Charger: " };
        ddDc = new RDropdown { Prefix = "Battery: " };
        foreach (var dd in new[] { ddAc, ddDc }) dd.Items.AddRange(profiles.Select(p => p.Name));
        void PushAuto()
        {
            if (!syncing) app.SetAutoSwitch(chkAuto.Checked, profileIds[ddAc.SelectedIndex], profileIds[ddDc.SelectedIndex]);
        }
        chkAuto.CheckedChanged += (_, _) => PushAuto();
        ddAc.SelectedIndexChanged += (_, _) => PushAuto();
        ddDc.SelectedIndexChanged += (_, _) => PushAuto();
        L.Row(3, 30, i => i switch { 0 => (Control)chkAuto, 1 => ddAc, _ => ddDc });
        tips.SetToolTip(chkAuto, TextWrap.Lines("Plugging in or unplugging switches to the profile you pick here"));
        L.Space(6);

        // ---- Footer: buttons, then one status line with the version at its right ------------------------
        int fh = L.S(70);
        var footer = new Panel { BackColor = Theme.ButtonSecond, Bounds = new Rectangle(0, L.Y, L.Width, fh) };
        int pad = L.Pad, bh = L.S(30), gap = L.Gap, top = L.S(9);
        var btnSettings = new RButton { Text = "Settings", Glyph = Glyphs.Gear, BorderColor = Theme.Accent, Bounds = new Rectangle(pad, top, L.S(112), bh) };
        btnSettings.Click += (_, _) => app.ShowSettings();
        tips.SetToolTip(btnSettings, TextWrap.Lines("UI scale, profile hotkey, start with Windows"));

        int bw = L.S(100);
        var btnQuit = new RButton { Text = "Quit", Bounds = new Rectangle(L.Width - pad - L.S(70), top, L.S(70), bh) };
        btnQuit.Click += (_, _) => app.Quit();
        btnRestore = new RButton { Text = "Restore", Glyph = Glyphs.Refresh, Bounds = new Rectangle(btnQuit.Left - gap - bw, top, bw, bh) };
        tips.SetToolTip(btnRestore, TextWrap.Lines("Put every setting Wyvern Center changed back to how it was before Wyvern Center"));
        btnRestore.Click += (_, _) => app.RestoreOriginals();
        // Whether Dragon Center / MSI Center could override Wyvern Center: hover for what to disable.
        lblMsiApps = new Label
        {
            Bounds = new Rectangle(btnSettings.Right + gap, top, btnRestore.Left - gap - btnSettings.Right - gap, bh),
            Font = Theme.UiFont(8.75f),
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
            UseMnemonic = false,
        };

        var rowTop = top + bh + L.S(5);
        var version = new Label
        {
            Text = "v" + AppController.Version,
            ForeColor = Theme.ForeDim,
            Font = Theme.UiFont(8.5f),
            Bounds = new Rectangle(L.Width - pad - L.S(50), rowTop, L.S(50), L.S(20)),
            TextAlign = ContentAlignment.MiddleRight,
        };
        lblStatus = new Label
        {
            ForeColor = Theme.ForeDim,
            Font = Theme.UiFont(8.5f),
            Bounds = new Rectangle(pad, rowTop, version.Left - pad - L.S(6), L.S(20)),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, // the whole text is in its tooltip
            UseMnemonic = false,
        };
        footer.Controls.AddRange(new Control[] { btnSettings, lblMsiApps, btnRestore, btnQuit, lblStatus, version });
        Controls.Add(footer);
        L.Y += fh;

        ClientSize = new Size(L.Width, L.Y);
        ResumeLayout(false);

        VisibleChanged += (_, _) =>
        {
            if (Visible && !viewing) { viewing = true; app.AddViewer(); }
            else if (!Visible && viewing) { viewing = false; app.RemoveViewer(); }
        };
        app.SampleArrived += OnSample;

        app.StateChanged += SyncUI;
        SyncUI();
    }



    private void OnSample()
    {
        if (IsDisposed || app.LastSample is not { } s) return;
        ShowSensors(app.LastSensors);
        if (s.BatteryPct is int pct)
            lblBatteryRight.Text = s.BatteryW is double bw && Math.Abs(bw) >= 0.05
                ? $"{pct}% · {(bw > 0 ? "charging" : "draining")} {Math.Abs(bw):F1} W"
                : $"{pct}%";
    }

    private static string Fan(int? raw) => raw is int f ? $"Fan {FanPercent.Of(f)}%" : "";

    private void ShowSensors(Sensors s)
    {
        if (IsDisposed) return;
        lblProfileRight.Text = s.CpuTemp is int t and > 0 ? $"CPU {t}°C  {Fan(s.CpuFan)}" : "";
        lblProfileRight.ForeColor = s.CpuTemp >= 90 ? Theme.Warn : Theme.ForeDim;
        lblCoolingRight.Text = s.GpuTemp is int g and > 0 ? $"GPU {g}°C  {Fan(s.GpuFan)}" : $"GPU asleep  {Fan(s.GpuFan)}";
        lblCoolingRight.ForeColor = s.GpuTemp >= 90 ? Theme.Warn : Theme.ForeDim;
    }

    private void SyncUI()
    {
        if (IsDisposed) return;
        syncing = true;
        var ec = app.Ec;
        Icon = app.AppIcon;
        ShowSensors(app.LastSensors);

        var active = app.ActiveProfile;
        lblProfile.Text = active is null ? "Profile: pick one" : $"Profile: {active.Name}";
        if (ec.ReadOnlyMode)
        {
            lblCardCpu.Text = "Read-only: " + (!app.IsAdmin ? "run as administrator to change settings."
                : !SecureStorage.Ready ? "the data folder isn't available (see the status line)." : app.Support switch
            {
                _ when app.Interface == FirmwareInterface.Wmi2 => "newer MSI laptops are read-only in this version (beta). See Settings → This laptop.",
                ModelSupport.SameBoard => "this firmware version isn't confirmed yet. See Settings → This laptop.",
                ModelSupport.Generic => "this MSI model has no profile yet. See Settings → This laptop.",
                ModelSupport.None => "no MSI firmware interface found on this laptop.",
                _ => "the data folder couldn't be prepared.",
            });
        }
        else if (active is null)
        {
            lblCardCpu.Text = "Pick a profile. Nothing is changed until you do.";
        }
        else
        {
            lblCardCpu.Text = ProfileText.Summary(active);
        }
        foreach (var p in app.Store.Profiles)
        {
            if (!profileButtons.TryGetValue(p.Id, out var b)) continue;
            b.Activated = app.Store.ActiveId == p.Id;
            b.Enabled = !ec.ReadOnlyMode;
            tips.SetToolTip(b, TextWrap.Lines($"{ProfileText.Cpu(p)}\n{ProfileText.Fans(p)}\n{ProfileText.Gpu(p)}"));
        }
        btnEdit.Enabled = true;
        if (btnNew is not null) btnNew.Enabled = !ec.ReadOnlyMode;
        foreach (var b in refreshButtons) b.Enabled = !ec.ReadOnlyMode; // read-only: no writes, Windows settings included

        btnOverlay.Activated = app.OverlayShown;
        tips.SetToolTip(btnOverlay, TextWrap.Lines($"In-game overlay: FPS, clocks, watts, temps, fan RPM ({app.OverlayHotkeyText})"));
        var boostOn = app.MaxCoolingOn;
        btnCoolerBoost.Activated = boostOn;
        btnCoolerBoost.Text = boostOn ? "Max Cooling ON" : "Max Cooling";
        btnCoolerBoost.Enabled = app.MaxCoolingAvailable;
        tips.SetToolTip(btnCoolerBoost, TextWrap.Lines(app.MaxCoolingAvailable
            ? "All fans to full speed (loud), like MSI's Cooler Boost. Click again to put your fans back."
            : "Needs a confirmed model whose fan curves Wyvern Center can set."));
        lblCooling.Text = app.FailsafeEngaged ? "Cooling: thermal failsafe (max fans)" : boostOn ? "Cooling: Cooler Boost" : "Cooling";
        lblCooling.ForeColor = app.FailsafeEngaged ? Theme.Warn : Theme.ForeMain;

        var charge = app.Cached("chargeLimit");
        var chargeOn = ec.IsAvailable("chargeLimit");
        lblBattery.Text = charge is int c ? $"Battery Charge Limit: {c}%" : "Battery Charge Limit";
        lblChargeOffIcon.Visible = lblChargeOff.Visible = !chargeOn;
        lblChargeOff.Text = app.Interface == FirmwareInterface.Wmi2 ? "Read-only (beta)" : "Disabled: MSI security issue (hover for details)";
        var chargeWhy = chargeOn ? "Stop charging at this level to slow battery wear"
            : app.Interface == FirmwareInterface.Wmi2 ? "Read-only on newer MSI laptops in this version (beta)."
            : "Disabled. On this laptop the charge limit can only be set through MSI's background service, which has a " +
              "critical security hole (CVE-2026-37452: any program can use it to take full control of Windows). " +
              "It stays off until MSI fixes that service.";
        foreach (var ctl in chargeButtons.Cast<Control>().Append(sliderCharge).Append(lblBattery).Append(lblChargeOff).Append(lblChargeOffIcon))
            tips.SetToolTip(ctl, TextWrap.Lines(chargeWhy));
        for (var i = 0; i < ChargePresets.Length; i++)
        {
            chargeButtons[i].Activated = charge == ChargePresets[i];
            chargeButtons[i].Enabled = ec.IsAvailable("chargeLimit");
        }
        sliderCharge.Enabled = ec.IsAvailable("chargeLimit");
        if (charge is int cv) sliderCharge.SetSilently(Math.Clamp(cv, 50, 100));

        chkAuto.Checked = app.Settings.AutoSwitchEnabled;
        ddAc.SetSilently(Math.Max(0, Array.IndexOf(profileIds, app.Store.OnAc)));
        ddDc.SetSilently(Math.Max(0, Array.IndexOf(profileIds, app.Store.OnBattery)));
        ddAc.Enabled = ddDc.Enabled = chkAuto.Checked;
        ddAc.Invalidate();
        ddDc.Invalidate();

        var hz = app.CurrentRefresh;
        lblScreen.Text = hz is int h ? $"Screen: {h}Hz" + (app.Settings.RefreshAuto ? " (Auto)" : "") : "Screen";
        refreshButtons[0].Activated = app.Settings.RefreshAuto;
        for (var i = 1; i < refreshButtons.Count; i++)
            refreshButtons[i].Activated = !app.Settings.RefreshAuto && refreshButtons[i].Text == $"{hz}Hz";

        var level = app.Cached("kbdBacklight");
        lblKeyboard.Text = level is int lv and >= 0 and < 4 ? $"Keyboard: backlight {BacklightNames[lv].ToLowerInvariant()}" : "Keyboard";
        for (var i = 0; i < backlightButtons.Count; i++)
        {
            backlightButtons[i].Activated = level == i;
            backlightButtons[i].Enabled = ec.IsAvailable("kbdBacklight");
        }
        lblMsiApps.Text = MsiServices.Summary(app.MsiApps) + "  ⓘ";
        lblMsiApps.ForeColor = app.MsiApps.Any(MsiServices.Interferes) ? Theme.Warn : Theme.ForeDim;
        tips.SetToolTip(lblMsiApps, TextWrap.Lines(MsiServices.Explain(app.MsiApps)));
        btnRestore!.Enabled = app.CanRestore && !ec.ReadOnlyMode;
        lblStatus.Text = (app.Busy ? "Working… " : "") + (app.FailsafeEngaged ? "THERMAL FAILSAFE ACTIVE · " : "")
                         + (app.HotkeyError is { } hk ? hk + " · " : "") + app.Status;
        tips.SetToolTip(lblStatus, TextWrap.Lines(lblStatus.Text));
        foreach (var b in Controls.OfType<RButton>()) b.Invalidate();
        syncing = false;
    }

    public void ShowNearTray()
    {
        if (!Visible)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        }
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        app.RefreshCache();
        SyncUI();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing really closes: the window is rebuilt next time it's opened, so it costs no memory meanwhile.
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (viewing) { viewing = false; app.RemoveViewer(); }
            // A charge-limit move still waiting for the slider to settle is saved, not lost, when the window closes.
            if (chargeDebounce.Enabled)
            {
                chargeDebounce.Stop();
                app.SetSetting("chargeLimit", sliderCharge.Value, "Charge limit");
            }
            app.StateChanged -= SyncUI;
            app.SampleArrived -= OnSample;
            chargeDebounce.Dispose();
            tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
