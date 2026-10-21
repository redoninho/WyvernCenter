using WyvernCenter.Core.Features;
using WyvernCenter.UI;

namespace WyvernCenter;

/// <summary>
/// Edits the profile in use: picking one at the top (or on the main window) switches to it, and this window follows. Left: CPU and GPU settings,
/// each explained by a hover ⓘ. Right: both fan curves, as large as the window allows. Edits save at once; edits to
/// the profile in use apply at once. Opens beside the main window at its height.
/// </summary>
public sealed class ProfilesForm : Form, IFitsHeight
{
    private static readonly (Turbo Value, string Label)[] Turbos =
    {
        (Turbo.Off, "Off"), (Turbo.Efficient, "Efficient"), (Turbo.Aggressive, "Aggressive"),
    };
    private static readonly (FirmwareLevel Value, string Label)[] Levels =
    {
        (FirmwareLevel.SuperBattery, "Super Battery"), (FirmwareLevel.Normal, "Normal"), (FirmwareLevel.Extreme, "Extreme"),
    };
    private static readonly (PowerOverlay Value, string Label)[] Overlays =
    {
        (PowerOverlay.Efficiency, "Best power efficiency"), (PowerOverlay.Balanced, "Balanced"), (PowerOverlay.Performance, "Best performance"),
    };
    /// <summary>The clock slider counts in 100 MHz steps; its top step means "no limit".</summary>
    private static int NoLimitStep => Presets.MaxMhz / 100;
    /// <summary>The GPU slider counts in 50 MHz steps; the step above the cap maximum means "no cap".</summary>
    private static int GpuNoCapStep => Presets.GpuCapMaxMhz / 50 + 1;
    /// <summary>The power slider counts in watts; the step above the GPU's limit means "no limit".</summary>
    private static int PowerNoLimitStep => Presets.GpuMaxW + 1;

    private const string TurboInfo =
        "How eagerly the CPU goes above its base clock.\n\n" +
        "Off: stays at or below the base clock. Least power and heat; slower under load.\n" +
        "Efficient: boosts when there's sustained work. A good balance.\n" +
        "Aggressive: boosts to full speed as soon as anything runs. Fastest; most power and heat.";
    private const string FirmwareInfo =
        "The laptop firmware's own power limits (what MSI Center calls its modes).\n\n" +
        "Super Battery: lowest limits; saves battery, slower under load.\n" +
        "Normal: standard limits.\n" +
        "Extreme: highest limits; lets the CPU draw the most power (runs hot).";
    private const string WindowsInfo =
        "The Windows power mode (Settings → System → Power).\n\n" +
        "Best power efficiency: background work runs slower, saves power.\n" +
        "Balanced: Windows' default.\n" +
        "Best performance: apps respond fastest, uses more power.";

    private readonly AppController app;
    private bool viewing;
    private string selected;
    private bool syncing, wasBusy;

    private readonly RDropdown ddProfile;
    private readonly RButton btnRename, btnDelete, btnReset;
    private readonly Label lblFeedback;
    private readonly CheckBox chkCycle;
    private readonly RButton tabCpu, tabGpu;
    private readonly Panel cpuPanel, gpuPanel, right;
    private readonly Label lblClock, lblGpuClock, lblGpuPower, lblGpuLive, gpuInfo, lblFansLive;
    private readonly RSlider sliderClock, sliderGpu, sliderPower;
    private readonly RDropdown ddTurbo, ddFirmware, ddWindows;
    private readonly CurveEditor cpuEditor, gpuEditor;
    private readonly List<RButton> fanTools;
    private readonly int editorsTop;
    private readonly System.Windows.Forms.Timer clockDebounce = new() { Interval = 450 };
    private readonly System.Windows.Forms.Timer gpuDebounce = new() { Interval = 450 };
    private readonly System.Windows.Forms.Timer powerDebounce = new() { Interval = 450 };
    private bool gpuTab;
    private readonly UiLayout gpuLayout;
    private List<string> listedIds = new();

    public ProfilesForm(AppController app)
    {
        this.app = app;
        selected = app.Store.ActiveId ?? Presets.Balanced;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.FormBack;
        ForeColor = Theme.ForeMain;
        Font = Theme.UiFont(10.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        Icon = app.AppIcon;

        // ---- Left: which profile, and its CPU / GPU settings ------------------------------------------------
        var left = new Panel { BackColor = Theme.FormBack };
        var L = new UiLayout(left, 380);
        L.Space(12);
        ddProfile = L.Row(1, 34, _ => new RDropdown { Prefix = "Profile: " })[0];
        ddProfile.SelectedIndexChanged += (_, _) =>
        {
            if (syncing || ddProfile.SelectedIndex < 0) return;
            var id = listedIds[ddProfile.SelectedIndex];
            if (app.Ec.ReadOnlyMode) Select(id); // can't switch: just look at it
            else app.UseProfile(id);             // the window follows the profile in use
        };
        var actions = L.Row(3, 30, _ => new RButton { Secondary = true, BorderColor = Theme.Gray });
        (btnRename, btnDelete, btnReset) = (actions[0], actions[1], actions[2]);
        btnRename.Text = "Rename";
        btnRename.Click += (_, _) => Rename();
        btnDelete.Text = "Delete";
        btnDelete.Click += (_, _) => Delete();
        btnReset.Text = "Reset";
        btnReset.Glyph = Glyphs.Refresh;
        btnReset.Click += (_, _) => Reset();
        chkCycle = L.Add(new CheckBox { ForeColor = Theme.ForeMain }, 24);
        chkCycle.CheckedChanged += (_, _) => { if (!syncing) app.SetInCycle(selected, chkCycle.Checked); };
        L.Tip(chkCycle, ProfileText.CycleHelp);
        lblFeedback = L.Text("", Theme.ForeDim, 8.75f, 22);
        lblFeedback.UseMnemonic = false;
        lblFeedback.AutoEllipsis = true;

        var tabs = L.Row(2, 32, i => new RButton { Text = i == 0 ? "CPU" : "GPU", Glyph = i == 0 ? Glyphs.Power : Glyphs.Screen, BorderColor = i == 0 ? Theme.Cpu : Theme.Gpu });
        (tabCpu, tabGpu) = (tabs[0], tabs[1]);
        tabCpu.Click += (_, _) => { gpuTab = false; Sync(); };
        tabGpu.Click += (_, _) => { gpuTab = true; Sync(); };
        cpuPanel = new Panel { BackColor = Theme.FormBack, Location = new Point(0, L.Y) };
        gpuPanel = new Panel { BackColor = Theme.FormBack, Location = new Point(0, L.Y) };
        left.Controls.Add(cpuPanel);
        left.Controls.Add(gpuPanel);

        var C = new UiLayout(cpuPanel, 380);
        (_, lblClock, _) = C.Header(Glyphs.Thermometer, "Max CPU clock", ProfileText.ClockHelp);
        lblClock.ForeColor = Theme.ForeMain;
        sliderClock = C.Slider(Presets.MinMhz / 100, NoLimitStep, Theme.Cpu, 1);
        sliderClock.LargeStep = 5;
        sliderClock.ValueChanged += (_, _) =>
        {
            ShowClock(sliderClock.Value == NoLimitStep ? null : sliderClock.Value * 100);
            if (!syncing) { clockDebounce.Stop(); clockDebounce.Start(); }
        };
        clockDebounce.Tick += (_, _) => FlushClock();
        C.Space(6);
        C.Header(Glyphs.Bolt, "Turbo", TurboInfo);
        ddTurbo = C.Add(new RDropdown(), 30);
        ddTurbo.Items.AddRange(Turbos.Select(t => t.Label));
        ddTurbo.SelectedIndexChanged += (_, _) => { if (!syncing) Edit(p => p.Turbo = Turbos[ddTurbo.SelectedIndex].Value); };
        C.Space(6);
        C.Header(Glyphs.Power, "Firmware power level", FirmwareInfo);
        ddFirmware = C.Add(new RDropdown(), 30);
        ddFirmware.Items.AddRange(Levels.Select(l => l.Label));
        ddFirmware.SelectedIndexChanged += (_, _) => { if (!syncing) Edit(p => p.Firmware = Levels[ddFirmware.SelectedIndex].Value); };
        C.Space(6);
        C.Header(Glyphs.Screen, "Windows power mode", WindowsInfo);
        ddWindows = C.Add(new RDropdown(), 30);
        ddWindows.Items.AddRange(Overlays.Select(o => o.Label));
        ddWindows.SelectedIndexChanged += (_, _) => { if (!syncing) Edit(p => p.Windows = Overlays[ddWindows.SelectedIndex].Value); };

        var G = gpuLayout = new UiLayout(gpuPanel, 380);
        (_, lblGpuClock, gpuInfo) = G.Header(Glyphs.Thermometer, "Max GPU clock", ProfileText.GpuHelp);
        lblGpuClock.ForeColor = Theme.ForeMain;
        sliderGpu = G.Slider(Presets.GpuMinMhz / 50, GpuNoCapStep, Theme.Gpu, 1);
        sliderGpu.LargeStep = 4;
        sliderGpu.ValueChanged += (_, _) =>
        {
            lblGpuClock.Text = ProfileText.GpuClockLabel(sliderGpu.Value == GpuNoCapStep ? null : sliderGpu.Value * 50);
            if (!syncing) { gpuDebounce.Stop(); gpuDebounce.Start(); }
        };
        gpuDebounce.Tick += (_, _) => FlushGpu();
        G.Space(6);
        (_, lblGpuPower, _) = G.Header(Glyphs.Bolt, "Max GPU power", ProfileText.GpuPowerHelp);
        lblGpuPower.ForeColor = Theme.ForeMain;
        sliderPower = G.Slider(Presets.GpuMinW, PowerNoLimitStep, Theme.Gpu, 1);
        sliderPower.LargeStep = 5;
        sliderPower.ValueChanged += (_, _) =>
        {
            lblGpuPower.Text = ProfileText.GpuPowerLabel(sliderPower.Value == PowerNoLimitStep ? null : sliderPower.Value);
            if (!syncing) { powerDebounce.Stop(); powerDebounce.Start(); }
        };
        powerDebounce.Tick += (_, _) => FlushGpuPower();
        G.Space(6);
        lblGpuLive = G.Text("", Theme.ForeMain, 9f, 22);
        lblGpuLive.UseMnemonic = false;
        lblGpuLive.AutoEllipsis = true;
        cpuPanel.Size = gpuPanel.Size = new Size(C.Width, Math.Max(C.Y, G.Y));
        L.Y += cpuPanel.Height;

        // ---- Right: the fan curves, filling the height -------------------------------------------------------
        right = new Panel { BackColor = Theme.FormBack };
        var R = new UiLayout(right, 620);
        R.Space(12);
        (_, lblFansLive, _) = R.Header(Glyphs.Thermometer, "Fan curves", ProfileText.FanHelp);
        lblFansLive.ForeColor = Theme.ForeDim;
        editorsTop = R.Y;
        cpuEditor = R.Add(new CurveEditor { Title = "CPU fan", Accent = Theme.Cpu }, 260);
        gpuEditor = R.Add(new CurveEditor { Title = "GPU fan", Accent = Theme.Gpu }, 260);
        cpuEditor.CurveChanged += (_, _) => { var v = cpuEditor.Speeds; Edit(p => p.CpuCurve = v); };
        gpuEditor.CurveChanged += (_, _) => { var v = gpuEditor.Speeds; Edit(p => p.GpuCurve = v); };
        fanTools = R.Row(2, 30, _ => new RButton { Secondary = true, BorderColor = Theme.Gray });
        fanTools[0].Text = "Copy CPU curve to GPU";
        fanTools[0].Click += (_, _) => Edit(p => p.GpuCurve = (int[])p.CpuCurve.Clone());
        fanTools[1].Text = "Max fans";
        fanTools[1].Glyph = Glyphs.Bolt;
        fanTools[1].Click += (_, _) =>
        {
            var max = Enumerable.Repeat(app.Ec.Profile?.Curve.MaxSpeed ?? FanPercent.Max, 6).ToArray();
            Edit(p => { p.CpuCurve = max; p.GpuCurve = (int[])max.Clone(); });
        };
        R.Space(4);

        var height = Math.Max(L.Y + L.S(12), R.Y);
        left.SetBounds(0, 0, L.Width, height);
        right.SetBounds(left.Right, 0, R.Width, height);
        Controls.AddRange(new Control[] { left, right });
        ClientSize = new Size(right.Right, height);
        ResumeLayout(false);

        app.StateChanged += OnState;
        app.SampleArrived += OnSample;
        app.ProfilesChanged += RebuildList;
        // Closing a form never raises VisibleChanged(false): Dispose ends the viewing too.
        VisibleChanged += (_, _) =>
        {
            if (Visible && !viewing) { viewing = true; app.AddViewer(); }
            else if (!Visible && viewing) { viewing = false; app.RemoveViewer(); }
        };
        Load += (_, _) => LoadThresholds();
        RebuildList();
    }

    /// <summary>Takes the given outer height: the fan curves grow (or shrink) to fill it; the left side keeps its size.</summary>
    public void FitHeight(int height)
    {
        var client = height - (Height - ClientSize.Height);
        var gap = Theme.S(8);
        var toolsH = fanTools[0].Height;
        var minClient = Math.Max(Controls[0].Height, editorsTop + 2 * Theme.S(180) + toolsH + 3 * gap + Theme.S(12));
        client = Math.Max(client, minClient);
        var editorH = (client - editorsTop - toolsH - 3 * gap - Theme.S(12)) / 2;
        cpuEditor.SetBounds(cpuEditor.Left, editorsTop, cpuEditor.Width, editorH);
        gpuEditor.SetBounds(gpuEditor.Left, cpuEditor.Bottom + gap, gpuEditor.Width, editorH);
        foreach (var b in fanTools) b.Top = gpuEditor.Bottom + gap;
        foreach (Control c in Controls) c.Height = client;
        ClientSize = new Size(ClientSize.Width, client);
    }

    /// <summary>Saves a clock change still waiting for the slider to settle (before switching profile or closing).</summary>
    private void FlushClock()
    {
        if (!clockDebounce.Enabled) return;
        clockDebounce.Stop();
        var step = sliderClock.Value;
        Edit(p => p.MaxClockMhz = step == NoLimitStep ? null : step * 100);
    }

    /// <summary>Saves a GPU power change still waiting for the slider to settle.</summary>
    private void FlushGpuPower()
    {
        if (!powerDebounce.Enabled) return;
        powerDebounce.Stop();
        var step = sliderPower.Value;
        Edit(p => p.GpuPowerW = step == PowerNoLimitStep ? null : step);
    }

    /// <summary>Saves a GPU clock change still waiting for the slider to settle.</summary>
    private void FlushGpu()
    {
        if (!gpuDebounce.Enabled) return;
        gpuDebounce.Stop();
        var step = sliderGpu.Value;
        Edit(p => p.GpuMaxClockMhz = step == GpuNoCapStep ? null : step * 50);
    }

    public string SelectedId => selected;
    private Profile Current => app.Store.Find(selected) ?? app.Store.Get(Presets.Balanced);
    private bool SelectedInUse => app.Store.ActiveId == selected;

    public void Select(string id)
    {
        if (app.Store.Find(id) is null) id = app.Store.ActiveId ?? Presets.Balanced;
        FlushClock();
        FlushGpu();
        FlushGpuPower();
        if (id != selected) lblFeedback.Text = "";
        selected = id;
        RebuildList();
    }

    /// <summary>The profile picker's entries (the one in use marked).</summary>
    private void RebuildList()
    {
        if (IsDisposed) return;
        if (app.Store.Find(selected) is null) selected = app.Store.ActiveId ?? Presets.Balanced;
        listedIds = app.Store.Profiles.Select(p => p.Id).ToList();
        ddProfile.Items.Clear();
        ddProfile.Items.AddRange(app.Store.Profiles.Select(p => p.Name));
        Sync();
    }

    private void ShowClock(int? mhz) =>
        lblClock.Text = ProfileText.ClockLabel(mhz) + (Current.Turbo == Turbo.Off ? $"  (turbo off: max {Presets.BaseMhz / 1000.0:F1})" : "");

    private void Sync()
    {
        if (IsDisposed) return;
        syncing = true;
        var p = Current;
        var inUse = SelectedInUse;
        var readOnly = app.Ec.ReadOnlyMode;

        Text = $"Edit profile — {p.Name}";
        ddProfile.SetSilently(listedIds.IndexOf(selected));
        btnRename.Enabled = btnDelete.Enabled = !readOnly && !p.BuiltIn;
        btnReset.Enabled = !readOnly && p.BuiltIn;
        chkCycle.Text = $"In hotkey cycle ({app.HotkeyText})";
        chkCycle.Checked = p.IsInCycle;
        chkCycle.Enabled = !readOnly;
        if (readOnly) Say("Read-only: changes can't be made on this laptop right now.", Theme.Warn);

        tabCpu.Activated = !gpuTab;
        tabGpu.Activated = gpuTab;
        cpuPanel.Visible = !gpuTab;
        gpuPanel.Visible = gpuTab;
        if (!gpuDebounce.Enabled)
        {
            sliderGpu.SetSilently(p.GpuMaxClockMhz is int gm ? gm / 50 : GpuNoCapStep);
            lblGpuClock.Text = ProfileText.GpuClockLabel(p.GpuMaxClockMhz);
        }
        sliderGpu.Enabled = sliderPower.Enabled = !readOnly && app.GpuClockSupported;
        if (!powerDebounce.Enabled)
        {
            sliderPower.SetSilently(p.GpuPowerW ?? PowerNoLimitStep);
            lblGpuPower.Text = ProfileText.GpuPowerLabel(p.GpuPowerW);
        }
        var gpuState = !Presets.Hardware.HasGpu ? "No NVIDIA GPU found: these settings do nothing here."
            : !app.GpuClockSupported ? "The NVIDIA driver doesn't allow a clock cap on this laptop."
            : !inUse ? "Applies when you use this profile."
            : app.GpuLastResult == GpuWrite.Failed ? "The last try failed; tried again when the GPU next wakes."
            : app.GpuEverAwake ? "Applied while the GPU is awake; put back each time it wakes."
            : "The GPU is asleep: the cap applies the next time it wakes.";
        gpuInfo.ForeColor = app.GpuClockSupported ? Theme.ForeDim : Theme.Warn;
        gpuLayout.SetInfo(gpuInfo,ProfileText.GpuHelp + "\n\nNow: " + gpuState);

        sliderClock.Maximum = p.Turbo == Turbo.Off ? Presets.BaseMhz / 100 : NoLimitStep;
        if (!clockDebounce.Enabled)
        {
            sliderClock.SetSilently(p.MaxClockMhz is int m ? m / 100 : NoLimitStep);
            ShowClock(p.MaxClockMhz);
        }
        ddTurbo.SetSilently(Array.FindIndex(Turbos, t => t.Value == p.Turbo));
        ddFirmware.SetSilently(Array.FindIndex(Levels, l => l.Value == p.Firmware));
        ddWindows.SetSilently(Array.FindIndex(Overlays, o => o.Value == p.Windows));
        foreach (var c in new Control[] { sliderClock, ddTurbo, ddFirmware, ddWindows }) c.Enabled = !readOnly;

        var fansWritable = FanCurveRules.Writable(app.Ec.Profile);
        foreach (var b in fanTools) b.Enabled = !readOnly && fansWritable;
        var failsafeHolds = inUse && app.FailsafeEngaged;
        foreach (var (editor, curve) in new[] { (cpuEditor, p.CpuCurve), (gpuEditor, p.GpuCurve) })
        {
            if (!editor.IsDragging) editor.Speeds = curve;
            editor.Dimmed = readOnly || failsafeHolds || !fansWritable;
            editor.OverlayText = failsafeHolds && fansWritable ? "Thermal failsafe: fans at maximum until it cools down."
                : !fansWritable ? "Fan curves can't be set on this model yet: the firmware controls the fans." : null;
        }
        foreach (var b in Controls.OfType<Panel>().SelectMany(x => x.Controls.OfType<RButton>())) b.Invalidate();
        syncing = false;
        OnSample();
    }

    /// <summary>Saves a change to the selected profile and says what happened.</summary>
    private void Edit(Action<Profile> change)
    {
        var errors = app.EditProfile(selected, change);
        if (errors.Count > 0)
        {
            Say(string.Join(" ", errors), Theme.Warn);
            Sync(); // put the controls back to the stored values
            return;
        }
        Say(SelectedInUse ? "Saved and applied." : "Saved.", Theme.ForeMain);
        Sync();
    }

    private void Rename()
    {
        var p = Current;
        if (TextPrompt.Ask(this, "Rename profile", "Name", p.Name) is not { } name) return;
        if (app.RenameProfile(p.Id, name) is { } error) Say(error, Theme.Warn);
        else Say("Renamed.", Theme.ForeMain);
    }

    private void Delete()
    {
        var p = Current;
        if (MessageBox.Show(this, $"Delete {p.Name}?", "Delete profile", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        app.DeleteProfile(p.Id);
        Select(app.Store.ActiveId ?? Presets.Balanced);
        Say($"Deleted {p.Name}.", Theme.ForeDim);
    }

    private void Reset()
    {
        var p = Current;
        if (MessageBox.Show(this, $"Put {p.Name} back to its shipped settings (CPU, GPU and fans)?", "Reset profile",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        app.ResetProfile(p.Id);
        Say(SelectedInUse ? $"{p.Name} reset and applied." : $"{p.Name} reset.", Theme.ForeMain);
        Sync();
    }

    private void LoadThresholds()
    {
        Task.Run(() =>
        {
            try { return (FanControl.ReadThresholds(app.Ec, "cpu"), FanControl.ReadThresholds(app.Ec, "gpu")); }
            catch { return ((int[]?)null, (int[]?)null); }
        }).ContinueWith(t => app.Post(() =>
        {
            if (IsDisposed) return;
            var (cpu, gpu) = t.Result;
            if (cpu is not null) cpuEditor.Thresholds = cpu;
            if (gpu is not null) gpuEditor.Thresholds = gpu;
        }));
    }

    private void OnSample()
    {
        if (IsDisposed) return;
        var s = app.LastSample;
        var live = SelectedInUse && s is not null;
        cpuEditor.Live = live && s!.CpuTemp is int ct and > 0 && s.CpuFan is int cf ? (ct, cf) : null;
        gpuEditor.Live = live && s!.GpuTemp is int gt and > 0 && s.GpuFan is int gf ? (gt, gf) : null;
        var cur = Current;
        lblGpuLive.Text = !Presets.Hardware.HasGpu ? ""
            : !app.GpuAwake ? "Now: GPU asleep (nothing is using it)"
            : app.GpuIdleBackedOff ? "Now: GPU idle, not polled so it can sleep"
            : app.LastGpu is { Mhz: double gmhz } nv
                ? $"Now: {gmhz:F0} MHz" + (nv.Watts is double gw ? $" · {gw:F1} W" : "")
                  + (SelectedInUse && cur.GpuPowerW is int target && app.GpuLimiterCap is int cap && cap < (cur.GpuMaxClockMhz ?? Presets.GpuTopMhz)
                      ? $"  (holding {target} W at {cap} MHz)" : "")
            : "Now: GPU awake" + (app.LastSensors.GpuTemp is int gt3 and > 0 ? $" · {gt3} °C" : "");
        if (!SelectedInUse) { lblFansLive.Text = "not in use: no live readings"; return; }
        if (s is null) { lblFansLive.Text = ""; return; }
        var cpu = s.CpuTemp is int t and > 0 ? $"CPU {t}°C" + (s.CpuFan is int f ? $" {FanPercent.Of(f)}%" : "") : "";
        var gpu = s.GpuTemp is int g and > 0 ? $"GPU {g}°C" + (s.GpuFan is int f2 ? $" {FanPercent.Of(f2)}%" : "") : "GPU asleep";
        lblFansLive.Text = $"{cpu}   {gpu}";
    }

    private void OnState()
    {
        if (IsDisposed) return;
        if (app.Busy) Say("Applying…", Theme.ForeDim);
        else if (wasBusy && lblFeedback.Text == "Applying…")
        {
            var ok = app.Status.EndsWith(": done");
            Say(ok ? "Saved and applied." : app.Status, ok ? Theme.ForeMain : Theme.Warn);
        }
        wasBusy = app.Busy;
        // Follow the profile in use (switched here, on the main window, by the hotkey or by auto-switch).
        if (app.Store.ActiveId is { } active && active != selected && !app.Ec.ReadOnlyMode) Select(active);
        else RebuildList();
    }

    private void Say(string text, Color color)
    {
        lblFeedback.Text = text;
        lblFeedback.ForeColor = color;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        FlushClock();
        FlushGpu();
        FlushGpuPower();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (viewing) { viewing = false; app.RemoveViewer(); }
            app.StateChanged -= OnState;
            app.SampleArrived -= OnSample;
            app.ProfilesChanged -= RebuildList;
            clockDebounce.Dispose();
            gpuDebounce.Dispose();
            powerDebounce.Dispose();
        }
        base.Dispose(disposing);
    }
}
