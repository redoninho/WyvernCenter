using System.Runtime.InteropServices;
using System.Security.Principal;
using WyvernCenter.Core.Features;
using WyvernCenter.Core.Monitoring;
using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Safety;
using WyvernCenter.Core.Scm;
using WyvernCenter.Core.Verify;
using WyvernCenter.Core.Wmi2;
using WyvernCenter.UI;
using Microsoft.Win32;

namespace WyvernCenter;

public readonly record struct Sensors(int? CpuTemp, int? CpuFan, int? GpuTemp, int? GpuFan);

/// <summary>
/// Owns the hardware objects, tray icon, timers and power events. Every hardware job runs on one ordered
/// <see cref="HardwareQueue"/>; the UI only reads cached values, so it never waits on hardware.
/// </summary>
public sealed class AppController : ApplicationContext
{
    public const string Version = "0.5.2";

    public AppSettings Settings { get; }
    public EcController Ec { get; }
    public string FirmwareId { get; } = "";
    /// <summary>The profile's name on a supported model, else what Windows calls the laptop.</summary>
    public string ModelName => Support == ModelSupport.Supported ? Ec.Profile!.Name : systemModel ?? "Unknown laptop";
    /// <summary>How well this laptop is known (see <see cref="ModelSupport"/>).</summary>
    public ModelSupport Support { get; } = ModelSupport.None;
    /// <summary>The MSI firmware interface found: SCM0 (WMI v1, up to 10th gen) or WMI v2 (11th gen and newer).</summary>
    public FirmwareInterface Interface { get; } = FirmwareInterface.Scm;
    private readonly Wmi2Ec? wmi2;
    // MSI's Dragon Center service (battery limit on WMI v1 laptops); does nothing until the user switches it on.
    private readonly WyvernCenter.Core.Bridge.MsiServiceBridge? bridge;
    /// <summary>The user allowed changes on this unconfirmed model (Settings → This laptop).</summary>
    public bool ExperimentalOn { get; }
    private readonly string? systemModel = SystemModel();
    private readonly IScmTransport scmTransport;
    public bool IsAdmin { get; }
    /// <summary>MSI's own apps' services (Dragon Center, MSI Center), refreshed with the cached settings.</summary>
    public IReadOnlyList<ServiceState> MsiApps { get; private set; } = Array.Empty<ServiceState>();
    private readonly WindowsServiceControl serviceControl = new();
    public string Status { get; private set; } = "";
    public bool FailsafeEngaged => failsafe.Engaged;
    public string? DisplayDevice { get; }
    public int[] RefreshRates { get; } = Array.Empty<int>();
    public Sensors LastSensors { get; private set; }
    public bool CanRestore { get; private set; }
    /// <summary>True while an action the user started is running (not background checks).</summary>
    public bool Busy => Volatile.Read(ref userJobs) > 0;
    private int userJobs;

    public event Action? StateChanged;
    /// <summary>Raised on the UI thread after each monitoring sample.</summary>
    public event Action? SampleArrived;

    public MonitorSample? LastSample { get; private set; }
    public IReadOnlyDictionary<string, MetricHistory> History => history;
    public const int HistorySeconds = 120;

    // Settings shown in the UI, refreshed on the hardware thread (a model without one just has nothing cached).
    private static readonly string[] CachedSettings = { "kbdBacklight", "coolerBoost", "chargeLimit" };
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int?> cache = new(); // written on worker threads, read on the UI thread

    private readonly HardwareQueue hw = new();
    private readonly WindowsCpuPower cpu;
    private readonly WindowsPowerOverlay overlay = new();
    private readonly ProfileApplier applier;
    private readonly ThermalFailsafe failsafe = new();
    private readonly FailsafeCooling failsafeCooling;
    // Fans held at full speed by the thermal failsafe and/or the user's Max Cooling. Only under failsafeLock.
    private readonly CoolingHold coolingHold;
    private readonly NotifyIcon tray;
    private readonly Control invoker = new();
    // The thermal failsafe runs on its own timer thread: a busy UI thread or a slow job on the hardware queue (e.g.
    // MSI's service starting) can never delay it. failsafeLock serialises it with the jobs that touch the fans.
    private System.Threading.Timer? failsafeTimer;
    private readonly object failsafeLock = new();
    private int failsafeRunning;
    private volatile bool quitting;
    private readonly WriteLog log;
    private MainForm? main;
    private int sampling;
    private int viewers;
    private readonly SystemMonitor monitor;
    private readonly WindowsCpuCounters cpuCounters = new();
    private readonly NvmlGpu nvml = new();
    private readonly GpuClockState gpuClock;
    private readonly Dictionary<string, MetricHistory> history = MetricKeys.All.ToDictionary(k => k, _ => new MetricHistory(HistorySeconds));
    private readonly System.Windows.Forms.Timer monitorTimer = new() { Interval = 1000 };

    public AppController(bool startHidden)
    {
        invoker.CreateControl();
        var storageWarning = SecureStorage.PrepareDataDir();
        Settings = SecureStorage.Ready ? AppSettings.Load() : new AppSettings();
        Theme.UserScale = Math.Clamp(Settings.UiScale, 70, 125) / 100f;
        IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        log = new WriteLog(Path.Combine(AppSettings.Dir, "log.txt"));
        cpu = new WindowsCpuPower(Path.Combine(AppSettings.Dir, "cpu-originals.json"));

        ModelProfile? profile = null;
        IScmTransport scm = new UnavailableScm();
        string? notFound = null;
        try
        {
            // SCM0 (WMI v1 laptops): MSI's root\WMI classes first, the proven route on the GF63. Those classes come from
            // MSI's software, so without it the same firmware blocks are opened by GUID.
            scm = new WmiScmTransport();
            FirmwareId = WyvernCenter.Core.Profile.FirmwareId.Parse(scm.ReadAll(ScmClass.Software));
        }
        catch (Exception ex)
        {
            notFound = ex.Message;
            scm = new UnavailableScm();
            var byGuid = new GuidScmTransport();
            if (IsAdmin && byGuid.Present())
            {
                log.Append("SCM0: MSI's WMI classes are missing; using the firmware GUIDs");
                scm = byGuid;
                try { FirmwareId = WyvernCenter.Core.Profile.FirmwareId.Parse(scm.ReadAll(ScmClass.Software)); }
                catch (Exception ex2) { notFound = ex2.Message; }
            }
            else byGuid.Dispose();
        }
        if (FirmwareId == "" && IsAdmin && GuidWmi2Transport.TryOpen(log.Append) is { } transport)
        {
            // WMI v2 (11th gen+): the firmware's MSI_ACPI GUID, read-only in this version.
            if (Wmi2Ec.Detect(transport, log.Append) is { } detected)
            {
                wmi2 = detected;
                Interface = FirmwareInterface.Wmi2;
                FirmwareId = detected.Firmware != "" ? detected.Firmware : "WMI2 (firmware not reported)";
            }
            else transport.Dispose();
        }
        if (FirmwareId != "")
        {
            var match = ProfileCatalog.Resolve(WyvernCenter.Core.Profile.FirmwareId.Version(FirmwareId), Interface);
            (profile, Support) = (match.Profile, match.Support);
        }
        else Status = IsAdmin ? "MSI firmware interface not found" + (notFound is null ? "." : ": " + notFound) : "Run as administrator to control the laptop.";
        scmTransport = scm;
        // Changes on an unconfirmed model only after the user allowed them for this exact firmware. WMI v2 laptops are
        // read-only in this version whatever the setting says.
        ExperimentalOn = Interface == FirmwareInterface.Scm && Support is ModelSupport.SameBoard or ModelSupport.Generic && FirmwareId != ""
            && Settings.ExperimentalFirmware == FirmwareId;
        if (storageWarning is not null) Status = storageWarning;
        if (Status == "")
            Status = Support switch
            {
                _ when Interface == FirmwareInterface.Wmi2 => "Newer MSI laptop (WMI v2): readings only in this version (beta).",
                ModelSupport.SameBoard when ExperimentalOn => $"Experimental: firmware {FirmwareId} isn't confirmed yet; changes are allowed.",
                ModelSupport.Generic when ExperimentalOn => "Experimental: generic MSI support; changes are allowed (no fan curves).",
                ModelSupport.SameBoard => $"Firmware {FirmwareId} isn't confirmed yet: monitoring only (Settings → This laptop).",
                ModelSupport.Generic => "This MSI model has no profile yet: monitoring only (Settings → This laptop).",
                _ => "",
            };

        // Fail closed: without a trusted place for the snapshot and log, no hardware writes.
        if (IsAdmin && SecureStorage.Ready)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (StartupTask.RepairIfOld() is { } repaired) log.Append(repaired);
                if (StartupTask.MigrateLegacy() is not { } note) return;
                log.Append(note);
                if (note.StartsWith("The old", StringComparison.Ordinal)) Post(() => { Status = note; Notify(); });
            });
        if (IsAdmin && Interface == FirmwareInterface.Scm)
            bridge = new WyvernCenter.Core.Bridge.MsiServiceBridge(log.Append,
                SecureStorage.Ready ? Path.Combine(AppSettings.Dir, "msi-service-revert.txt") : null) { Enabled = Settings.UseMsiService };
        Ec = new EcController(profile, new EcAccess(scm, wmi2, bridge), new SnapshotStore(Path.Combine(AppSettings.Dir, "snapshot.json")), log,
            canWrite: IsAdmin && SecureStorage.Ready && Interface == FirmwareInterface.Scm && (Support == ModelSupport.Supported || ExperimentalOn),
            canTest: IsAdmin && SecureStorage.Ready);
        if (Settings.Profiles is null && (Settings.Mode ?? Settings.ModeOnAc ?? Settings.ModeOnBattery) is not null)
        {
            var (active, onAc, onBattery) = ProfileStore.Migrate(Settings.Mode, Settings.ModeOnAc, Settings.ModeOnBattery);
            Settings.ActiveProfile = active;
            Settings.ProfileOnAc = onAc;
            Settings.ProfileOnBattery = onBattery;
        }
        Settings.Mode = Settings.ModeOnAc = Settings.ModeOnBattery = null;
        Presets.Hardware = DetectHardware();
        Store = new ProfileStore(Settings.Profiles, Settings.ActiveProfile, Settings.ProfileOnAc, Settings.ProfileOnBattery, profile?.Curve);
        applier = new ProfileApplier(Ec, cpu, overlay);
        SaveProfiles();
        failsafeCooling = new FailsafeCooling(Ec);
        coolingHold = new CoolingHold(failsafeCooling);
        monitor = new SystemMonitor(new EcSensors(Ec), cpuCounters, new GpuWhenWatched(this, nvml), new WindowsBattery(), new WindowsMemory());
        gpuClock = new GpuClockState(nvml);
        gpuWatcher = new GpuWatcher(nvml);
        // Every second; the tick itself decides whether anything is needed, and closes NVML when it isn't.
        gpuTimer.Tick += (_, _) => hw.TryEnqueueOnce("gpu", GpuTick);
        gpuTimer.Start();
        monitorTimer.Tick += (_, _) => Sample();
        trimTimer.Tick += (_, _) => TrimIdle();

        DisplayDevice = WyvernCenter.Core.Features.DisplayRefresh.InternalDisplay();
        if (DisplayDevice is not null) RefreshRates = WyvernCenter.Core.Features.DisplayRefresh.SupportedRates(DisplayDevice);

        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer() };
        tray = new NotifyIcon { Text = "Wyvern Center", ContextMenuStrip = menu, Visible = true };
        menu.Opening += (_, _) => BuildMenu(menu);
        tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (main is { IsDisposed: false, Visible: true }) main.Close(); else ShowMain();
        };
        UpdateTrayIcon();

        failsafeTimer = new System.Threading.Timer(_ => RunFailsafe(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Re-apply the profile in use at launch (EC values can reset on reboot); first run changes nothing.
        var onAcNow = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline;
        // Nothing picked (first run or after Restore) stays nothing picked until the user chooses.
        var startProfile = Store.ActiveId is null ? null : AutoSwitch.OnPowerChange(null, onAcNow, AutoSwitchNow) ?? Store.ActiveId;
        lastOnAc = onAcNow;
        if (startProfile is { } id && !Ec.ReadOnlyMode) UseProfile(id);
        ProfilesChanged += RebuildMain;
        hotkey.Pressed += CycleProfile;
        RegisterHotkey();
        overlayHotkey.Pressed += ToggleOverlay;
        RegisterOverlayHotkey();
        if (Settings.OverlayOn) Post(() => ShowOverlay(true));
        if (HotkeyError is { } hotkeyError) Post(() => tray.ShowBalloonTip(5000, "Wyvern Center", hotkeyError, ToolTipIcon.Info));
        if (Settings.RefreshAuto) ApplyAutoRefresh();
        // One short use of MSI's service at start, if switched on: read the charge limit and put it back if it was lost.
        hw.Enqueue(() => { RefreshCacheNow(includeBridge: Settings.UseMsiService); ReapplyChargeLimit(); Notify(); });

        if (!startHidden) ShowMain();
        else trimTimer.Start(); // started for the tray: shrink once start-up is done
    }

    // ---- UI plumbing ----------------------------------------------------------------

    public void Post(Action action)
    {
        if (quitting || invoker.IsDisposed) return;
        invoker.BeginInvoke(() => { if (!quitting) action(); }); // callbacks queued before Quit must not touch disposed objects
    }

    private void Notify() => Post(() => { UpdateTrayIcon(); StateChanged?.Invoke(); });

    /// <summary>The main window is built when opened and disposed when closed: nothing of it stays in memory meanwhile.</summary>
    public void ShowMain()
    {
        if (main is null || main.IsDisposed) main = new MainForm(this);
        main.ShowNearTray();
    }

    private ProfilesForm? profilesForm;

    /// <summary>Opens the Profiles window on a profile (the one in use by default); one window, brought forward if open.</summary>
    public void ShowProfiles(string? selectId = null)
    {
        var fresh = profilesForm is null || profilesForm.IsDisposed;
        if (fresh) profilesForm = new ProfilesForm(this);
        profilesForm!.Select(selectId ?? Store.ActiveId ?? Presets.Balanced);
        if (fresh) PlaceBeside(profilesForm, matchHeight: true);
        profilesForm.Show();
        if (profilesForm.WindowState == FormWindowState.Minimized) profilesForm.WindowState = FormWindowState.Normal;
        profilesForm.Activate();
    }

    /// <summary>A new profile of the user's own (a copy of the one in use): put in use (same settings) and opened for editing.</summary>
    public void AddProfileFromMain()
    {
        if (NewProfile(Store.ActiveId ?? Presets.Balanced) is { } p)
        {
            UseProfile(p.Id);
            ShowProfiles(p.Id);
        }
        else
        {
            Status = LastProfileError ?? "Couldn't add a profile.";
            Notify();
        }
    }

    /// <summary>
    /// Opens a window beside the main one instead of in the middle of the screen: to its left, top-aligned, left of
    /// any of Wyvern Center's other windows already there. With <paramref name="matchHeight"/> the window takes the main
    /// window's height (it lays itself out for it). Without the main window open, the same spot by the tray.
    /// </summary>
    private void PlaceBeside(Form f, bool matchHeight)
    {
        const int gap = 10;
        var anchor = main is { IsDisposed: false, Visible: true } m ? m.Bounds : (Rectangle?)null;
        var area = anchor is { } a ? Screen.FromRectangle(a).WorkingArea : Screen.PrimaryScreen!.WorkingArea;
        if (matchHeight && anchor is { } mh && f is IFitsHeight fits) fits.FitHeight(Math.Min(mh.Height, area.Height));
        else if (matchHeight && f is IFitsHeight fitsArea) fitsArea.FitHeight(Math.Min(area.Height - 2 * gap, Theme.S(900)));
        var right = anchor?.Left - gap ?? area.Right - gap;
        var top = anchor?.Top ?? Math.Max(area.Top + gap, area.Bottom - gap - f.Height);
        var others = new Form?[] { profilesForm, monitorForm, settingsForm }
            .Where(o => o is { IsDisposed: false, Visible: true } && !ReferenceEquals(o, f)).Select(o => o!.Bounds).ToList();
        var x = right - f.Width;
        var start = x;
        for (var moved = true; moved;)
        {
            moved = false;
            foreach (var o in others)
                if (x < o.Right && x + f.Width > o.Left && top < o.Bottom && top + f.Height > o.Top)
                {
                    x = o.Left - gap - f.Width;
                    moved = true;
                }
        }
        // No room left of the others: take the slot beside the main window (on top of the older window).
        if (x < area.Left) x = start;
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(Math.Max(area.Left, x), Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - f.Height)));
    }

    /// <summary>The profile buttons are built from the list: rebuild the main window when profiles are added or removed.</summary>
    private void RebuildMain()
    {
        if (main is null || main.IsDisposed) { main = null; return; }
        var visible = main.Visible;
        var location = main.Location;
        main.Dispose();
        main = null;
        if (!visible) return;
        main = new MainForm(this) { Location = location };
        main.Show();
    }

    private MonitorForm? monitorForm;

    /// <summary>
    /// True while the Monitor window is open. Only then is the NVIDIA GPU queried: polling it every second would stop
    /// an idle Optimus GPU from sleeping.
    /// </summary>
    public bool GpuDetail => monitorForm is { IsDisposed: false, Visible: true } || OverlayShown || main is { IsDisposed: false, Visible: true };

    public void ShowMonitor()
    {
        if (monitorForm is null || monitorForm.IsDisposed)
        {
            monitorForm = new MonitorForm(this);
            // On the hardware thread, so it can't race a tick that is about to open NVML.
            monitorForm.VisibleChanged += (_, _) => { if (!GpuDetail) hw.Enqueue(() => { if (!gpuWatcher.Polling) nvml.Release(); }); };
            PlaceBeside(monitorForm, matchHeight: true);
        }
        monitorForm.Show();
        monitorForm.Activate();
    }

    /// <summary>Last value read on the hardware thread (null = unknown/unavailable).</summary>
    public int? Cached(string name) => cache.TryGetValue(name, out var v) ? v : null;

    /// <summary>Queues hardware work, then reports the outcome and refreshes cached values.</summary>
    private void RunHw(string what, Func<IEnumerable<SetResult>> work)
    {
        Interlocked.Increment(ref userJobs);
        Notify();
        hw.Enqueue(() =>
        {
            try
            {
            List<SetResult> results;
            try { results = work().ToList(); }
            catch (Exception ex) { results = new() { new SetResult(what, SetOutcome.Failed, ex.Message) }; }
            var bad = results.Where(r => !r.Ok).ToList();
            Status = bad.Count == 0 ? $"{what}: done" : $"{what}: " + string.Join("; ", bad.Select(r => $"{r.Setting} {r.Message}"));
            RefreshCacheNow();
            }
            finally
            {
                Interlocked.Decrement(ref userJobs);
                Notify();
            }
        });
    }

    /// <summary>Reads UI-visible values on the hardware thread.</summary>
    /// <param name="includeBridge">Also read settings behind MSI's service even if that starts it (start-up, switch on).</param>
    public void RefreshCache(bool includeBridge = false) => hw.Enqueue(() => { RefreshCacheNow(includeBridge); Notify(); });

    private void RefreshCacheNow(bool includeBridge = false)
    {
        foreach (var n in CachedSettings)
        {
            // Reading through MSI's service starts it: only when asked, or while it's already running (e.g. after a change).
            if (Ec.Profile?.Settings.TryGetValue(n, out var d) == true && d.Route == Route.Bridge
                && !(includeBridge || Ec.BridgeReady) && cache.ContainsKey(n)) continue;
            cache[n] = Ec.TryGet(n);
        }
        MsiApps = MsiServices.Installed(serviceControl, MsiServices.Known);
        CanRestore = Ec.CanRestore;
    }

    /// <summary>Windows that show live data register here; sampling runs every second only while one is open.</summary>
    public void AddViewer()
    {
        trimTimer.Stop();
        if (++viewers == 1) { monitorTimer.Start(); Sample(); }
    }

    public void RemoveViewer()
    {
        if (viewers > 0 && --viewers == 0)
        {
            monitorTimer.Stop();
            trimTimer.Stop();
            trimTimer.Start(); // back to a lean tray app in a few seconds
        }
    }

    // Nothing watching for a few seconds: close the CPU counters, compact the heap and give memory back to Windows.
    private readonly System.Windows.Forms.Timer trimTimer = new() { Interval = 3000 };

    private void TrimIdle()
    {
        trimTimer.Stop();
        if (viewers > 0) return;
        Task.Run(() =>
        {
            cpuCounters.Release();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(self.Handle, -1, -1);
        });
    }

    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    // Screen brightness for the power estimate (WMI), read at most every 5 s while sampling.
    public int? Brightness { get; private set; }
    private DateTime brightnessAt;

    private void ReadBrightness()
    {
        if (DateTime.Now - brightnessAt < TimeSpan.FromSeconds(5)) return;
        brightnessAt = DateTime.Now;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (System.Management.ManagementObject o in searcher.Get())
                using (o) Brightness = Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch
        {
            Brightness = null;
        }
    }

    /// <summary>Where the watts go now (null before the first sample).</summary>
    public PowerTotal? Power => LastSample is { } s
        ? PowerBreakdown.Compute(s, Brightness, Cached("kbdBacklight"), s.GpuTemp is int t ? t > 0 : null,
            SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline)
        : null;

    /// <summary>One monitoring sample off the UI thread; skipped if the previous one is still running.</summary>
    private void Sample()
    {
        if (Interlocked.Exchange(ref sampling, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                var s = monitor.Sample();
                ReadBrightness();
                LastSample = s;
                LastSensors = new Sensors(s.CpuTemp, s.CpuFan, s.GpuTemp, s.GpuFan);
                Post(() =>
                {
                    foreach (var (key, value) in MetricKeys.Values(s)) history[key].Add(value);
                    UpdateTrayIcon();
                    SampleArrived?.Invoke();
                });
            }
            catch
            {
                // a failed sample just leaves a gap
            }
            finally
            {
                Interlocked.Exchange(ref sampling, 0);
            }
        });
    }

    // ---- Profiles --------------------------------------------------------------------------
    // The store holds every profile; exactly one is in use (Store.ActiveId). Edits save at once and, for the profile
    // in use, apply at once. Nothing touches hardware until the user picks a profile.

    public ProfileStore Store { get; }
    public Profile? ActiveProfile => Store.Active;
    /// <summary>Raised when profiles are added, removed or renamed.</summary>
    public event Action? ProfilesChanged;
    /// <summary>The profile last sent to the hardware: what the failsafe hands the fans back to (read on the hardware thread).</summary>
    private volatile Profile? applied;

    private void SaveProfiles()
    {
        Settings.Profiles = Store.Profiles.Select(p => p.Clone()).ToList();
        Settings.ActiveProfile = Store.ActiveId;
        Settings.ProfileOnAc = Store.OnAc;
        Settings.ProfileOnBattery = Store.OnBattery;
        Settings.Save();
        if (AppSettings.SaveError is { } error) Status = "Couldn't save settings: " + error;
    }

    public void UseProfile(string id)
    {
        if (Ec.ReadOnlyMode) return; // read-only means no writes at all, Windows settings included
        var p = Store.Get(id).Clone();
        Store.ActiveId = id;
        applied = p;
        if (Settings.OriginalPowerMode is null && overlay.Get() is { } before) Settings.OriginalPowerMode = before; // for Restore
        SaveProfiles();
        RunHw(p.Name, () =>
        {
            gpuWatcher.Limiter.Reset(); // a new profile starts from its own clock cap
            // While the fans are held at max (thermal failsafe or Max Cooling), apply the rest; release puts this
            // profile's curves back. Under the failsafe lock so an engage can't interleave its curve writes with ours.
            List<SetResult> results;
            bool holdFans, byUser;
            lock (failsafeLock)
            {
                holdFans = coolingHold.Active;
                byUser = coolingHold.User && !coolingHold.Thermal;
                if (holdFans) coolingHold.UserChangedFans();
                results = applier.Apply(p, skipFans: holdFans).ToList();
            }
            if (SyncGpuClock(Ec.TryGet("gpuTemp")))
                results.Add(gpuClock.LastResult switch
                {
                    GpuWrite.Ok => new SetResult("gpuClock", SetOutcome.Ok, ProfileText.GpuClockLabel(p.GpuMaxClockMhz)),
                    GpuWrite.NotSupported => new SetResult("gpuClock", SetOutcome.Rejected, "the NVIDIA driver doesn't allow a clock cap"),
                    _ => new SetResult("gpuClock", SetOutcome.Failed, "couldn't set it; tried again when the GPU next wakes"),
                });
            if (holdFans)
                results.Add(new SetResult("fans", SetOutcome.Ok, byUser ? "at full speed until Max Cooling is turned off" : "held by thermal failsafe until it cools down"));
            return results;
        });
    }

    /// <returns>Validation errors (nothing saved), or empty when saved (and applied, for the profile in use).</returns>
    public IReadOnlyList<string> EditProfile(string id, Action<Profile> change)
    {
        var errors = Store.Edit(id, change);
        if (errors.Count > 0) return errors;
        SaveProfiles();
        if (Store.ActiveId == id) UseProfile(id); else Notify();
        return errors;
    }

    /// <summary>Hotkey cycle membership: saved, nothing re-applied.</summary>
    public void SetInCycle(string id, bool on)
    {
        Store.SetInCycle(id, on);
        SaveProfiles();
        Notify();
    }

    /// <summary>Why the last New/Duplicate didn't happen (e.g. already 3 own profiles).</summary>
    public string? LastProfileError { get; private set; }

    public Profile? NewProfile(string fromId) => TryAdd(() => Store.New(fromId));

    private Profile? TryAdd(Func<Profile> add)
    {
        try
        {
            LastProfileError = null;
            return Changed(add());
        }
        catch (InvalidOperationException ex)
        {
            LastProfileError = ex.Message;
            return null;
        }
    }

    public Profile? DuplicateProfile(string id) => TryAdd(() => Store.Duplicate(id));

    /// <returns>An error message, or null when renamed.</returns>
    public string? RenameProfile(string id, string name)
    {
        try { Store.Rename(id, name); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ex.Message; }
        Changed(Store.Get(id));
        return null;
    }

    public void DeleteProfile(string id)
    {
        var wasActive = Store.ActiveId == id;
        Store.Delete(id);
        Changed(null);
        if (wasActive) UseProfile(Store.ActiveId!);
    }

    public void ResetProfile(string id)
    {
        Store.Reset(id);
        SaveProfiles();
        if (Store.ActiveId == id) UseProfile(id); else Notify();
    }

    private Profile Changed(Profile? p)
    {
        SaveProfiles();
        Post(() => ProfilesChanged?.Invoke());
        Notify();
        return p!;
    }

    // ---- Profile hotkey ---------------------------------------------------------------------------

    private readonly HotkeyWindow hotkey = new();
    private ProfilePopup? popup;
    public string? HotkeyError { get; private set; }
    public string HotkeyText => HotkeyWindow.Describe(Settings.HotkeyModifiers, Settings.HotkeyKey);

    private void RegisterHotkey()
    {
        HotkeyError = hotkey.Register(Settings.HotkeyModifiers, Settings.HotkeyKey) ? null : $"{HotkeyText} is taken by another app; pick another in Settings.";
        Notify();
    }

    /// <returns>An error message, or null when the new combination is registered and saved.</returns>
    public string? SetHotkey(uint mods, int key) => SetHotkey(overlay: false, mods, key);

    public string? SetHotkey(bool overlay, uint mods, int key)
    {
        if (overlay) return SetOverlayHotkey(mods, key);
        if (mods == Settings.OverlayHotkeyModifiers && key == Settings.OverlayHotkeyKey) return "That's already the overlay hotkey; pick another.";
        if ((mods & (HotkeyWindow.ModControl | HotkeyWindow.ModAlt)) == 0) return "Use Ctrl or Alt in the combination.";
        if (HotkeyWindow.IsReserved(mods, key)) return $"{HotkeyWindow.Describe(mods, key)} is used by almost every app; pick another.";
        if (!hotkey.Register(mods, key))
        {
            var taken = $"{HotkeyWindow.Describe(mods, key)} is taken by another app; pick another.";
            RegisterHotkey(); // keep the old one
            return taken;
        }
        Settings.HotkeyModifiers = mods;
        Settings.HotkeyKey = key;
        Settings.Save();
        HotkeyError = null;
        Notify();
        return null;
    }

    public void CycleProfile()
    {
        popup ??= new ProfilePopup();
        if (Ec.ReadOnlyMode) { popup.ShowMessage(null, "Read-only", "Run Wyvern Center as administrator to switch profiles."); return; }
        if (Store.NextInCycle(Store.ActiveId) is not { } id) { popup.ShowMessage(null, "No profiles in the hotkey cycle", "Tick \"In hotkey cycle\" in Edit profiles."); return; }
        UseProfile(id);
        var p = Store.Get(id);
        popup.ShowMessage(p, p.Name, ProfileText.Summary(p));
    }

    /// <summary>Stops the hotkey while Settings records a new combination (so pressing the old one doesn't switch).</summary>
    public void PauseHotkey(bool paused)
    {
        if (paused) { hotkey.Unregister(); overlayHotkey.Unregister(); }
        else { RegisterHotkey(); RegisterOverlayHotkey(); }
    }

    // ---- In-game overlay -----------------------------------------------------------------------------
    // Shown: counts as a monitoring viewer and as "GPU detail" (readings flow, idle back-off still applies), and runs
    // the ETW frame counter. Hidden: all of that stops.

    private readonly HotkeyWindow overlayHotkey = new();
    private OverlayForm? overlayForm;
    private EtwPresentSession? fpsSession;
    private readonly FpsTracker fps = new();
    private int? lastForeground;
    private string? fpsError;
    public bool OverlayShown { get; private set; }
    public string? OverlayHotkeyError { get; private set; }
    public string OverlayHotkeyText => HotkeyWindow.Describe(Settings.OverlayHotkeyModifiers, Settings.OverlayHotkeyKey);

    private void RegisterOverlayHotkey()
    {
        OverlayHotkeyError = overlayHotkey.Register(Settings.OverlayHotkeyModifiers, Settings.OverlayHotkeyKey)
            ? null : $"{OverlayHotkeyText} is taken by another app; pick another in Settings.";
        Notify();
    }

    private string? SetOverlayHotkey(uint mods, int key)
    {
        if (mods == Settings.HotkeyModifiers && key == Settings.HotkeyKey) return "That's already the profile hotkey; pick another.";
        if ((mods & (HotkeyWindow.ModControl | HotkeyWindow.ModAlt)) == 0) return "Use Ctrl or Alt in the combination.";
        if (HotkeyWindow.IsReserved(mods, key)) return $"{HotkeyWindow.Describe(mods, key)} is used by almost every app; pick another.";
        if (!overlayHotkey.Register(mods, key))
        {
            var taken = $"{HotkeyWindow.Describe(mods, key)} is taken by another app; pick another.";
            RegisterOverlayHotkey();
            return taken;
        }
        Settings.OverlayHotkeyModifiers = mods;
        Settings.OverlayHotkeyKey = key;
        Settings.Save();
        OverlayHotkeyError = null;
        Notify();
        return null;
    }

    public void ToggleOverlay() => ShowOverlay(!OverlayShown);

    public void ShowOverlay(bool show)
    {
        if (show == OverlayShown) return;
        OverlayShown = show;
        Settings.OverlayOn = show;
        Settings.Save();
        if (show)
        {
            overlayForm ??= new OverlayForm();
            fpsSession = new EtwPresentSession();
            fpsError = IsAdmin ? fpsSession.Start(fps) : "needs administrator";
            SampleArrived += RenderOverlay;
            AddViewer();
            RenderOverlay();
        }
        else
        {
            SampleArrived -= RenderOverlay;
            RemoveViewer();
            fpsSession?.Dispose();
            fpsSession = null;
            overlayForm?.Hide();
        }
        Notify();
    }

    /// <summary>Settings changed (opacity, corner, lines): draw again now.</summary>
    public void OverlaySettingsChanged()
    {
        Settings.Save();
        if (OverlayShown) RenderOverlay();
    }

    /// <summary>The overlay's FPS graph: the last minute, one value a second (null = not presenting).</summary>
    private const int FpsHistorySeconds = 60;
    private readonly Queue<double?> fpsHistory = new();

    private void RenderOverlay()
    {
        if (!OverlayShown || overlayForm is null || overlayForm.IsDisposed) return;
        var (pid, app) = ForegroundApp();
        var frames = fps.Frames(pid, DateTime.UtcNow.ToFileTimeUtc());
        fpsHistory.Enqueue(frames?.Fps);
        while (fpsHistory.Count > FpsHistorySeconds) fpsHistory.Dequeue();
        var lines = OverlayText.Lines(
            new OverlayInput(LastSample, frames, frames is null ? null : app, ActiveProfile?.Name, GpuIdleBackedOff, Power, fpsHistory.ToList(), fpsError),
            new OverlayOptions(Settings.OverlayFps, Settings.OverlayCpu, Settings.OverlayGpu, Settings.OverlaySys, Settings.OverlayPower, Settings.OverlayGraph));
        overlayForm.Render(lines, Settings.OverlayOpacity, Settings.OverlayCorner);
    }

    /// <summary>The app in front (the game); Wyvern Center itself is skipped so clicking its window keeps the game's FPS.</summary>
    private (int? Pid, string? Name) ForegroundApp()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            int? pid = hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out var raw) != 0 ? (int)raw : null;
            if (pid == Environment.ProcessId) pid = lastForeground;
            lastForeground = pid;
            if (pid is not int id) return (null, null);
            using var p = System.Diagnostics.Process.GetProcessById(id);
            return (id, p.ProcessName);
        }
        catch
        {
            return (null, null);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    // ---- Settings / UI scale ---------------------------------------------------------------------

    private SettingsForm? settingsForm;

    public void ShowSettings()
    {
        if (settingsForm is null || settingsForm.IsDisposed) settingsForm = new SettingsForm(this);
        if (!settingsForm.Visible) PlaceBeside(settingsForm, matchHeight: false);
        settingsForm.Show();
        settingsForm.Activate();
    }

    private VerifyForm? verifyForm;

    public void ShowVerify()
    {
        if (!CanVerify) return;
        if (verifyForm is null || verifyForm.IsDisposed) verifyForm = new VerifyForm(this);
        verifyForm.Show();
        verifyForm.Activate();
    }

    /// <summary>Changes the UI scale and rebuilds the open windows in place.</summary>
    public void SetUiScale(int percent)
    {
        Settings.UiScale = percent;
        Settings.Save();
        Theme.UserScale = percent / 100f;
        RebuildMain();
        // Reopened windows are placed and sized again beside the (resized) main window.
        if (profilesForm is { IsDisposed: false, Visible: true } pf)
        {
            var sel = pf.SelectedId;
            pf.Close();
            ShowProfiles(sel);
        }
        if (monitorForm is { IsDisposed: false, Visible: true } mf)
        {
            mf.Close();
            ShowMonitor();
        }
        if (settingsForm is { IsDisposed: false, Visible: true } sf)
        {
            sf.Close();
            ShowSettings();
        }
    }

    // ---- GPU clock cap ---------------------------------------------------------------------------
    // Only touched while the GPU is awake (the EC reports its temperature); re-applied after every sleep.

    /// <summary>An NVIDIA GPU is present and accepts clock locks (no NVIDIA driver = no GPU controls).</summary>
    public bool GpuClockSupported => Presets.Hardware.HasGpu && gpuClock.Supported;
    public bool GpuEverAwake => gpuClock.EverAwake;
    public bool GpuAwake => LastSensors.GpuTemp is > 0;

    /// <summary>Hardware thread: bring the GPU clock cap in line with the applied profile, if the GPU is awake.</summary>
    /// <returns>True when the GPU was written to.</returns>
    private bool SyncGpuClock(int? gpuTemp)
    {
        if (applied is not { } p) return false;
        try
        {
            var wrote = gpuClock.Update(gpuWatcher.Limiter.Effective(p.GpuMaxClockMhz, p.GpuPowerW), gpuTemp is > 0);
            if (wrote && !gpuWatcher.Polling && !GpuDetail) nvml.Release(); // let the GPU sleep again
            return wrote;
        }
        catch (Exception ex)
        {
            log.Append("gpu clock: " + ex.Message); // an NVML problem must never stop the caller (e.g. the failsafe)
            return false;
        }
    }

    public GpuWrite? GpuLastResult => gpuClock.LastResult;

    // ---- GPU power cap and readings ------------------------------------------------------------------
    // A 1 s hardware-thread tick, only while the profile in use has a power limit or the Monitor window is open.
    // The poll policy keeps it from asking an idle or sleeping GPU anything, so the GPU can still sleep.

    private readonly GpuWatcher gpuWatcher;
    private readonly System.Windows.Forms.Timer gpuTimer = new() { Interval = 1000 };

    /// <summary>Latest NVIDIA reading (clock, load, watts); null while the GPU sleeps or isn't being asked.</summary>
    public GpuReading? LastGpu => gpuWatcher.Last;
    public bool GpuIdleBackedOff => gpuWatcher.Policy.BackedOff;
    public int? GpuLimiterCap => gpuWatcher.Limiter.Cap;

    private void GpuTick()
    {
        try
        {
            var p = applied;
            var limit = p is { GpuPowerW: not null } && gpuClock.Supported;
            var needed = GpuDetail || limit;
            // While nothing watches and the 5 s failsafe saw the GPU asleep, use that reading instead of polling the EC.
            var temp = !needed ? null : GpuDetail || LastSensors.GpuTemp is > 0 ? Ec.TryGet("gpuTemp") : LastSensors.GpuTemp;
            if (gpuWatcher.Tick(needed, temp, limit ? p!.GpuPowerW : null, p?.GpuMaxClockMhz)) SyncGpuClock(temp);
        }
        catch (Exception ex)
        {
            log.Append("gpu tick: " + ex.Message);
        }
    }

    // ---- Single settings ----------------------------------------------------------------

    public void SetSetting(string name, int value, string label) => RunHw(label, () =>
    {
        var r = Ec.Set(name, value);
        if (name == "chargeLimit" && r.Ok) Post(() => { Settings.ChargeLimit = value; Settings.Save(); });
        return new[] { r };
    });

    /// <summary>
    /// Hardware queue, at start-up: if the laptop's charge limit no longer matches the one the user set (the EC can lose
    /// it on a full power-off), set it again. Only with MSI's service switched on; one short service start.
    /// </summary>
    private void ReapplyChargeLimit()
    {
        if (!Settings.UseMsiService || Settings.ChargeLimit is not int want || !Ec.IsAvailable("chargeLimit")) return;
        if (Ec.TryGet("chargeLimit") is not int now || now == want) return;
        var r = Ec.Set("chargeLimit", want);
        log.Append($"start-up: charge limit was {now}%, set back to {want}%: {r.Message}");
        cache["chargeLimit"] = Ec.TryGet("chargeLimit");
    }

    // ---- Max Cooling (Wyvern's Cooler Boost) -------------------------------------------------

    /// <summary>The user's Max Cooling is on (fans at full speed through the firmware fan curves).</summary>
    public bool MaxCoolingOn
    {
        get { lock (failsafeLock) return coolingHold.User; }
    }

    /// <summary>This laptop's fan curves can be written (a confirmed model, not read-only).</summary>
    public bool MaxCoolingAvailable => !Ec.ReadOnlyMode && coolingHold.Available;

    /// <summary>
    /// All fans to full speed, the way MSI's Cooler Boost does, but through the firmware's own fan curves (no MSI
    /// software involved). Off puts back the fans that were running before (or the profile picked meanwhile).
    /// </summary>
    public void SetMaxCooling(bool on)
    {
        if (!MaxCoolingAvailable) return;
        RunHw(on ? "Max Cooling" : "Max Cooling off", () => MaxCoolingNow(on));
    }

    /// <summary>Hardware queue only.</summary>
    private IReadOnlyList<SetResult> MaxCoolingNow(bool on)
    {
        lock (failsafeLock)
        {
            var (results, released) = coolingHold.SetUser(on);
            if (released is { UserFanPending: true }) PutFansBack();
            return results;
        }
    }

    // ---- Verify my laptop ----------------------------------------------------------------------

    private VerifySession? verify;
    private string VerifyFile => Path.Combine(AppSettings.Dir, "verify.md");

    /// <summary>The wizard can run: admin rights, a safe data folder and an MSI firmware interface.</summary>
    public bool CanVerify => IsAdmin && SecureStorage.Ready && Ec.Profile is not null;

    public IReadOnlyList<VerifyStep> VerifySteps() => VerifyPlan.For(Ec, MaxCoolingAvailable);

    /// <summary>Starts the test session (WMI v2 writes allowed only inside it). <paramref name="done"/> on the UI thread.</summary>
    public void VerifyBegin(Action<bool> done) => hw.Enqueue(() =>
    {
        verify ??= new VerifySession(Ec, MaxCoolingAvailable ? MaxCoolingNow : null);
        var ok = verify.Begin();
        if (ok && wmi2 is not null) wmi2.WritesEnabled = true;
        Post(() => done(ok));
    });

    /// <summary>Live temperatures and fans for the readings step.</summary>
    public void VerifySensors(Action<string> done) => hw.Enqueue(() =>
    {
        string Temp(string n) => Ec.TryGet(n) is int t and > 0 ? $"{t} °C" : "–";
        string Rpm(string side) => FanRpm.FromTach(Ec.TryGet($"{side}FanTachLo"), Ec.TryGet($"{side}FanTachHi")) is int r ? $"{r} RPM" : "–";
        var text = $"CPU {Temp("cpuTemp")} · fan {Rpm("cpu")}\nGPU {Temp("gpuTemp")} · fan {Rpm("gpu")}";
        Post(() => done(text));
    });

    /// <summary>Reads a watched setting; <paramref name="done"/> gets "what it is now" and how often it changed.</summary>
    public void VerifyTrack(string setting, Action<string, int> done) => hw.Enqueue(() =>
    {
        if (verify is null) return;
        var v = verify.Track(setting);
        var changes = verify.Changes(setting);
        Post(() => done(v is int value ? DescribeSetting(setting, value) : "can't read it", changes));
    });

    public void VerifyApply(VerifyStep step, Action<bool, string> done) => hw.Enqueue(() =>
    {
        var (ok, note) = verify?.Apply(step) ?? (false, "not started");
        RefreshCacheNow();
        Post(() => { done(ok, note); Notify(); });
    });

    public void VerifyRevert(VerifyStep step, Action<bool, string> done) => hw.Enqueue(() =>
    {
        var (ok, note) = verify?.Revert(step) ?? (true, "");
        RefreshCacheNow();
        Post(() => { done(ok, note); Notify(); });
    });

    /// <summary>Runs one fan-curve check operation (see <see cref="VerifySession"/>) on the hardware queue.</summary>
    public void VerifyRun(Func<VerifySession, (bool Ok, string Note)> op, Action<bool, string> done) => hw.Enqueue(() =>
    {
        var (ok, note) = verify is null ? (false, "not started") : op(verify);
        RefreshCacheNow();
        Post(() => { done(ok, note); Notify(); });
    });

    /// <summary>The CPU fan's speed (the GPU fan's when the CPU one can't be read), for the fan-curve check.</summary>
    public void VerifyFanRpm(Action<int?> done) => hw.Enqueue(() =>
    {
        var rpm = FanRpm.FromTach(Ec.TryGet("cpuFanTachLo"), Ec.TryGet("cpuFanTachHi"))
                  ?? FanRpm.FromTach(Ec.TryGet("gpuFanTachLo"), Ec.TryGet("gpuFanTachHi"));
        Post(() => done(rpm));
    });

    public void VerifyRead(string setting, Action<int?> done) => hw.Enqueue(() =>
    {
        var v = Ec.TryGet(setting);
        Post(() => done(v));
    });

    /// <summary>The MSI level names for a measure step: the level now and the one it will try.</summary>
    public void VerifyLevelNames(VerifyStep step, Action<string, string> done) => hw.Enqueue(() =>
    {
        var def = step.Setting is { } s ? Ec.Profile?.Settings.GetValueOrDefault(s) : null;
        string Name(int? v) => VerifyPlan.LevelName(def?.Values?.FirstOrDefault(kv => kv.Value == v).Key);
        var now = step.Setting is { } n ? Ec.TryGet(n) : null;
        var test = def is not null && now is int cur ? VerifyPlan.TestValue(SettingDefView.Of(def), cur) : null;
        Post(() => done(Name(now), Name(test)));
    });

    // The measure step's CPU load. Its own deadline stops it even if the wizard stops feeding samples.
    private readonly object verifyLoadLock = new();
    private CpuLoad? verifyLoad;
    private System.Threading.Timer? verifyLoadDeadline;

    public void VerifyLoad(bool on)
    {
        lock (verifyLoadLock)
        {
            verifyLoadDeadline?.Dispose();
            verifyLoadDeadline = null;
            verifyLoad?.Stop();
            verifyLoad = null;
            if (!on) return;
            verifyLoad = new CpuLoad(Environment.ProcessorCount);
            verifyLoad.Start();
            verifyLoadDeadline = new System.Threading.Timer(_ => VerifyLoad(false), null, ModeMeasurement.DeadlineSeconds * 1000, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Ends the session: anything still changed goes back, WMI v2 writes go off again, and the answers are kept for
    /// the next support report.
    /// </summary>
    public void VerifyEnd(IReadOnlyList<VerifyResult> results) => hw.Enqueue(() => EndVerifyNow(results));

    private void EndVerifyNow(IReadOnlyList<VerifyResult>? results)
    {
        VerifyLoad(false);
        if (verify is not null)
        {
            foreach (var note in verify.End().Where(n => n != "")) log.Append("verify: " + note);
            verify = null;
        }
        if (wmi2 is not null) wmi2.WritesEnabled = false;
        if (results is { Count: > 0 } && SecureStorage.Ready)
        {
            try { File.WriteAllText(VerifyFile, VerifyPlan.ToMarkdown(results, FirmwareId, DateTime.Now)); }
            catch (Exception ex) { log.Append("verify: couldn't save the answers: " + ex.Message); }
        }
        RefreshCacheNow();
        Notify();
    }

    /// <summary>A setting's value in words (mode names, levels, on/off).</summary>
    private string DescribeSetting(string setting, int value)
    {
        var def = Ec.Profile?.Settings.GetValueOrDefault(setting);
        var name = def?.Values?.FirstOrDefault(kv => kv.Value == value).Key;
        return setting switch
        {
            "kbdBacklight" => $"Level {value}",
            "coolerBoost" => value == 1 ? "On" : "Off",
            "chargeLimit" => $"{value}%",
            "shiftMode" => $"{VerifyPlan.LevelName(name)} ({VerifySession.Show(setting, value)})",
            _ => name is null ? VerifySession.Show(setting, value) : $"{char.ToUpperInvariant(name[0])}{name[1..]} ({VerifySession.Show(setting, value)})",
        };
    }

    /// <summary>
    /// After a hold that couldn't simply undo itself: the original fans after a Restore (or with no profile picked),
    /// else the profile's own curves. Under the failsafe lock.
    /// </summary>
    private void PutFansBack()
    {
        if (restoreFansAfterFailsafe || applied is null) Ec.RestoreSnapshot(IsFanSetting);
        else FanControl.ApplyCurves(Ec, applied.CpuCurve, applied.GpuCurve);
        restoreFansAfterFailsafe = false;
    }

    // ---- Display ----------------------------------------------------------------------

    public int? CurrentRefresh => DisplayDevice is null ? null : WyvernCenter.Core.Features.DisplayRefresh.Current(DisplayDevice)?.Hz;

    public void SetRefresh(int hz)
    {
        if (Ec.ReadOnlyMode) return; // read-only means no writes at all, Windows settings included
        Settings.RefreshAuto = false;
        Settings.Save();
        if (DisplayDevice is not null) WyvernCenter.Core.Features.DisplayRefresh.Set(DisplayDevice, hz);
        Notify();
    }

    public void SetRefreshAuto()
    {
        if (Ec.ReadOnlyMode) return;
        Settings.RefreshAuto = true;
        Settings.Save();
        ApplyAutoRefresh();
        Notify();
    }

    private void ApplyAutoRefresh()
    {
        if (Ec.ReadOnlyMode || DisplayDevice is null || RefreshRates.Length == 0) return;
        var onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        var hz = onBattery ? RefreshRates.Min() : RefreshRates.Max();
        if (CurrentRefresh != hz) WyvernCenter.Core.Features.DisplayRefresh.Set(DisplayDevice, hz);
    }

    // ---- Restore ------------------------------------------------------------------------

    public void RestoreOriginals()
    {
        Store.ActiveId = null; // back to "nothing picked": Wyvern Center writes nothing until a profile is chosen again
        applied = null;
        Settings.ChargeLimit = null; // the original limit comes back and stays (not re-applied at start-up)
        var powerMode = Settings.OriginalPowerMode;
        SaveProfiles();
        RunHw("Restore originals", () =>
        {
            List<SetResult> results;
            lock (failsafeLock)
            {
                coolingHold.SetUser(false); // Restore means the originals: Max Cooling goes off too
                if (coolingHold.Active)
                {
                    // The failsafe owns the fans: restore the rest now and the fans when it releases.
                    results = Ec.RestoreSnapshot(n => !IsFanSetting(n)).ToList();
                    coolingHold.UserChangedFans();
                    restoreFansAfterFailsafe = true;
                    results.Add(new SetResult("fans", SetOutcome.Ok, "restored when the thermal failsafe releases"));
                }
                else results = Ec.RestoreSnapshot().ToList();
            }
            try
            {
                // No cap = the GPU's original state. Done even if the GPU sleeps (wakes it briefly) so no cap
                // lingers, but skipped when no cap can be in force.
                if (gpuClock.ResetIfCapped() && !GpuDetail) nvml.Release();
            }
            catch (Exception ex)
            {
                results.Add(new SetResult("gpuClock", SetOutcome.Failed, ex.Message));
            }
            try { cpu.RestoreOriginals(); }
            catch (Exception ex) { results.Add(new SetResult("cpu", SetOutcome.Failed, ex.Message)); }
            if (powerMode is { } mode)
            {
                try
                {
                    overlay.Set(mode);
                    results.Add(new SetResult("powerMode", SetOutcome.Ok, mode.ToString()));
                    Post(() => { Settings.OriginalPowerMode = null; Settings.Save(); }); // forget it only once it's back
                }
                catch (Exception ex) { results.Add(new SetResult("powerMode", SetOutcome.Failed, ex.Message)); }
            }
            return results;
        });
    }

    // ---- Failsafe and power events (hardware thread) --------------------------------------------

    private static bool IsFanSetting(string name) => name == "fanMode" || name.Contains("CurveSpeed");

    /// <summary>A Restore ran while the failsafe held the fans: put the original fans back when it releases.</summary>
    private bool restoreFansAfterFailsafe;

    /// <summary>Timer thread, every 5 s; skips a tick if the previous one is still running.</summary>
    private void RunFailsafe()
    {
        if (quitting || Interlocked.Exchange(ref failsafeRunning, 1) == 1) return;
        try
        {
            lock (failsafeLock) FailsafeTick();
        }
        catch (Exception ex)
        {
            log.Append("failsafe tick: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref failsafeRunning, 0);
        }
    }

    private void FailsafeTick()
    {
        if (Ec.ReadOnlyMode) return;
        var s = new Sensors(Ec.TryGet("cpuTemp"), Ec.TryGet("cpuFan"), Ec.TryGet("gpuTemp"), Ec.TryGet("gpuFan"));
        LastSensors = s;
        switch (failsafe.Update(s.CpuTemp, s.GpuTemp))
        {
            case FailsafeAction.Engage:
                log.Append($"failsafe: engage at CPU {s.CpuTemp}°C / GPU {s.GpuTemp}°C");
                var wasHeld = coolingHold.Active;
                var engaged = coolingHold.SetThermal(true).Results;
                Status = !coolingHold.Available
                    ? $"Thermal failsafe (CPU {s.CpuTemp}°C, GPU {s.GpuTemp}°C): fans left to the firmware's automatic control; this model's fan curves can't be set yet."
                    : wasHeld || engaged.Any(r => r.Ok)
                        ? $"Thermal failsafe: max cooling (CPU {s.CpuTemp}°C, GPU {s.GpuTemp}°C)"
                        : "Thermal failsafe could not raise the fans!";
                Post(() =>
                {
                    tray.ShowBalloonTip(5000, "Wyvern Center", Status, ToolTipIcon.Warning);
                });
                RefreshCache();
                break;

            case FailsafeAction.Release:
                log.Append($"failsafe: release at CPU {s.CpuTemp}°C / GPU {s.GpuTemp}°C");
                var (_, released) = coolingHold.SetThermal(false);
                if (released is { UserFanPending: true }) PutFansBack();
                Status = coolingHold.User
                    ? "Thermal failsafe released: Max Cooling is still on."
                    : "Thermal failsafe released: your fan settings are back.";
                RefreshCache();
                break;

            default:
                if (coolingHold.Active && coolingHold.Enforce().Count > 0)
                    log.Append("max cooling re-asserted");
                break;
        }
        var gpuTemp = s.GpuTemp;
        hw.TryEnqueueOnce("gpusync", () => SyncGpuClock(gpuTemp)); // GPU state lives on the hardware thread
        Notify();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.StatusChange:
                Post(() =>
                {
                    // Also sent for battery-percent steps: only a real plug/unplug may switch profile.
                    var onAc = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline;
                    var id = AutoSwitch.OnPowerChange(lastOnAc, onAc, AutoSwitchNow);
                    lastOnAc = onAc;
                    if (id is not null && id != Store.ActiveId) UseProfile(id);
                    if (Settings.RefreshAuto) ApplyAutoRefresh();
                    Notify();
                });
                break;
            case PowerModes.Resume:
                // The EC can drop custom values across sleep; put the user's mode back.
                Post(() => { if (Store.ActiveId is { } id) UseProfile(id); });
                break;
        }
    }

    private bool? lastOnAc;

    private static string? SystemModel()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var o in s.Get())
                using (o)
                {
                    var model = (o["Model"] as string)?.Trim();
                    return string.IsNullOrEmpty(model) ? null : model;
                }
        }
        catch
        {
            // shown as "Unknown laptop"
        }
        return null;
    }

    /// <summary>Allows (or stops) changes on this unconfirmed model, for this firmware only; applies after a restart.</summary>
    /// <summary>Whether the "Use MSI's Dragon Center service" switch applies here (WMI v1 laptop, admin).</summary>
    public bool MsiServiceSwitchShown => bridge is not null;
    /// <summary>MSI's service (MSI Foundation Service) is installed.</summary>
    public bool MsiServiceInstalled => bridge?.Installed ?? false;

    /// <summary>Switches the battery-limit route through MSI's Dragon Center service on or off (live).</summary>
    public void SetUseMsiService(bool on)
    {
        Settings.UseMsiService = on;
        Settings.Save();
        if (bridge is null) return;
        bridge.Enabled = on;
        if (on) RefreshCache(includeBridge: true);
        else
        {
            cache.TryRemove("chargeLimit", out _);
            Notify();
        }
    }

    public void SetExperimental(bool on)
    {
        // Opting out: put the originals back now, while changes are still allowed; afterwards the app is read-only and
        // couldn't restore them (or run the failsafe).
        if (!on && !Ec.ReadOnlyMode && Ec.CanRestore) RestoreOriginals();
        Settings.ExperimentalFirmware = on ? FirmwareId : null;
        Settings.Save();
        Status = "Restart Wyvern Center to apply: tray icon → Quit, then start it again.";
        Notify();
    }

    /// <summary>
    /// Saves a support report (and the ACPI DSDT table) and opens its folder. Reads the hardware only.
    /// <paramref name="done"/> gets the folder, or an error message, on the UI thread.
    /// </summary>
    public void CreateSupportReport(Action<string?, string?> done) => hw.Enqueue(() =>
    {
        try
        {
            var folder = WriteSupportReport();
            Post(() => done(folder, null));
            // Full path: this process is elevated, and a bare name would also search the app's (maybe user-writable) folder.
            try { System.Diagnostics.Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{folder}\""); }
            catch { }
        }
        catch (Exception ex)
        {
            Post(() => done(null, ex.Message));
        }
    });

    private string WriteSupportReport()
    {
        string Wmi(string cls, string prop, string where = "")
        {
            try
            {
                using var s = new System.Management.ManagementObjectSearcher($"SELECT {prop} FROM {cls}{where}");
                return string.Join(" + ", s.Get().Cast<System.Management.ManagementBaseObject>().Select(o => { using (o) return (o[prop]?.ToString() ?? "").Trim(); }).Where(v => v != ""));
            }
            catch { return "?"; }
        }
        var scm = SupportReport.ClassesToDump.ToDictionary(c => c, c =>
        {
            try { return scmTransport.ReadAll(c); } catch { return (IReadOnlyList<int>?)null; }
        });
        var services = (Ec.Profile?.MsiServices ?? ProfileCatalog.Generic.MsiServices).Select(n =>
        {
            try { using var sc = new System.ServiceProcess.ServiceController(n); return (n, sc.Status.ToString()); }
            catch { return (n, "not installed"); }
        }).ToList();
        var model = Wmi("Win32_ComputerSystem", "Model");
        var name = string.Concat($"{model} {DateTime.Now:yyyy-MM-dd HHmm}".Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        // Written inside the admin-only data folder, never to a user-writable place like the Desktop: this process is
        // elevated, and a non-admin could swap such a folder for a link into a protected one. The user gets read access
        // to just this report folder so they can attach the files.
        if (!SecureStorage.Ready) throw new InvalidOperationException("the data folder isn't available");
        var folder = Path.Combine(AppSettings.Dir, "reports", name);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        if (WindowsIdentity.GetCurrent().User is { } me)
        {
            var info = new DirectoryInfo(folder);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(me,
                System.Security.AccessControl.FileSystemRights.ReadAndExecute,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        string? dsdtFile = null;
        if (SupportReport.ReadDsdt() is { } dsdt)
        {
            dsdtFile = "dsdt.aml";
            File.WriteAllBytes(Path.Combine(folder, dsdtFile), dsdt);
        }
        var report = new SupportReport.Info(Version, Wmi("Win32_ComputerSystem", "Manufacturer"), model, Wmi("Win32_ComputerSystem", "SystemFamily"),
            Wmi("Win32_BaseBoard", "Product"), Wmi("Win32_BIOS", "SMBIOSBIOSVersion"), Wmi("Win32_Processor", "Name"),
            // Real GPUs only: virtual display adapters (remote-desktop and second-screen apps) would list the user's software.
            Wmi("Win32_VideoController", "Name", " WHERE PNPDeviceID LIKE 'PCI%'").Split(" + "), FirmwareId, Support, Ec.Profile?.Name, scm, services, dsdtFile,
            wmi2 is null ? null : Wmi2Dump.Read(wmi2),
            File.Exists(VerifyFile) ? File.ReadAllText(VerifyFile) : null);
        File.WriteAllText(Path.Combine(folder, "report.md"), SupportReport.Build(report));
        return folder;
    }

    /// <summary>
    /// This machine's CPU and GPU ranges (presets and sliders scale to them). The GPU's are read through NVML once and
    /// remembered, since reading them wakes the GPU; with no NVIDIA driver there are no GPU controls.
    /// </summary>
    private HardwareLimits DetectHardware()
    {
        // Remembered limits are used even if the driver is briefly missing (mid-update at logon): profiles keep their
        // GPU caps. Without remembered or readable limits there are no GPU controls this start, and nothing is fitted.
        var gpu = HardwareDetect.Plausible(Settings.GpuLimits) ? Settings.GpuLimits : null;
        if (gpu is null && NvmlGpu.DriverPresent)
        {
            gpu = nvml.ReadLimits();
            nvml.Release(); // let it sleep again
            if (HardwareDetect.Plausible(gpu)) Settings.GpuLimits = gpu;
            else gpu = null;
        }
        return HardwareDetect.Detect(gpu);
    }

    private AutoSwitchSettings AutoSwitchNow => new(Settings.AutoSwitchEnabled, Store.OnAc, Store.OnBattery);

    public void SetAutoSwitch(bool enabled, string onAcId, string onBatteryId)
    {
        Settings.AutoSwitchEnabled = enabled;
        Store.OnAc = onAcId;
        Store.OnBattery = onBatteryId;
        SaveProfiles();
        var onAcNow = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline;
        if (AutoSwitch.ProfileFor(onAcNow, AutoSwitchNow) is { } id && id != Store.ActiveId) UseProfile(id);
        Notify();
    }

    // ---- Tray -------------------------------------------------------------------------------

    public static Color ProfileColor(Profile? p) => p?.Id switch
    {
        null => Theme.Gray,
        Presets.Battery => Theme.Eco,
        Presets.Quiet => Color.FromArgb(20, 196, 196),
        Presets.Balanced => Theme.Standard,
        Presets.Performance => Theme.Custom,
        Presets.Extreme => Theme.Turbo,
        _ => Theme.Accent,
    };

    /// <summary>The wyvern icon (all sizes) for windows.</summary>
    public Icon AppIcon => appIcon ??= new Icon(IconStream());

    /// <summary>The same icon at the tray's size, picked from the .ico rather than scaled (it's pixel art).</summary>
    private Icon TrayIcon => trayIcon ??= new Icon(IconStream(), SystemInformation.SmallIconSize);

    private Icon? appIcon, trayIcon;

    private static Stream IconStream() =>
        typeof(AppController).Assembly.GetManifestResourceStream("app.ico") ?? throw new InvalidOperationException("app.ico missing from the build");

    private void UpdateTrayIcon()
    {
        if (quitting) return;
        tray.Icon = TrayIcon;
        var t = LastSensors.CpuTemp;
        var w = LastSample?.CpuW;
        var text = $"Wyvern Center · {ActiveProfile?.Name ?? "no profile picked"}" + (t is > 0 ? $" · CPU {t}°C" : "") + (w is double ww ? $" {ww:F0} W" : "");
        tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add("Open Wyvern Center", null, (_, _) => ShowMain());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var p in Store.Profiles)
        {
            var id = p.Id;
            menu.Items.Add(new ToolStripMenuItem(p.Name, null, (_, _) => UseProfile(id))
            {
                Checked = Store.ActiveId == id,
                Enabled = !Ec.ReadOnlyMode,
                ToolTipText = ProfileText.Cpu(p),
            });
        }
        menu.Items.Add("Edit profiles…", null, (_, _) => ShowProfiles());
        menu.Items.Add("Monitor…", null, (_, _) => ShowMonitor());
        menu.Items.Add(new ToolStripMenuItem("Overlay", null, (_, _) => ToggleOverlay()) { Checked = OverlayShown });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Max Cooling", null, (_, _) => SetMaxCooling(!MaxCoolingOn))
        {
            Checked = MaxCoolingOn,
            Enabled = MaxCoolingAvailable,
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Restore original settings", null, (_, _) => RestoreOriginals()) { Enabled = CanRestore && !Ec.ReadOnlyMode });
        menu.Items.Add("Quit", null, (_, _) => Quit());
    }

    public void Quit()
    {
        if (quitting) return;
        quitting = true;
        var done = new ManualResetEvent(false);
        if (failsafeTimer?.Dispose(done) == true && done.WaitOne(TimeSpan.FromSeconds(10)))
            done.Dispose(); // only once signalled: a tick still running would otherwise signal a disposed handle and crash
        // The software power limiter may have stepped the clock lock down: leave the profile's own cap behind instead.
        hw.Enqueue(() => { gpuWatcher.Limiter.Reset(); SyncGpuClock(Ec.TryGet("gpuTemp")); });
        hw.Enqueue(() => { if (verify is not null) EndVerifyNow(null); }); // a test change never outlives the app
        // Max Cooling ends with the app: put the fans back rather than leave them at full speed.
        hw.Enqueue(() =>
        {
            lock (failsafeLock)
            {
                if (!coolingHold.User) return;
                var (_, released) = coolingHold.SetUser(false);
                if (released is { UserFanPending: true }) PutFansBack();
            }
        });
        hotkey.Dispose();
        overlayHotkey.Dispose();
        popup?.Dispose();
        fpsSession?.Dispose();
        overlayForm?.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        gpuTimer.Stop();
        monitorTimer.Stop();
        tray.Visible = false;
        hw.WaitIdle(TimeSpan.FromSeconds(15)); // never leave a half-written curve behind
        bridge?.Dispose(); // stops MSI's service if we started it, and sets it back to Disabled if it was
        hw.Dispose();
        tray.Dispose();
        cpuCounters.Dispose();
        nvml.Dispose();
        appIcon?.Dispose();
        trayIcon?.Dispose();
        ExitThread();
    }

    /// <summary>Stand-in when the firmware interface can't be opened: every read/write fails cleanly.</summary>
    private sealed class UnavailableScm : IScmTransport
    {
        public int Read(ScmClass c, int index) => throw new InvalidOperationException("SCM0 unavailable");
        public void Write(ScmClass c, int index, int value) => throw new InvalidOperationException("SCM0 unavailable");
        public IReadOnlyList<int> ReadAll(ScmClass c) => throw new InvalidOperationException("SCM0 unavailable");
    }

    /// <summary>The NVIDIA source, consulted only while the Monitor window is open.</summary>
    private sealed class GpuWhenWatched : IGpuSource
    {
        private readonly AppController _app;
        private readonly NvmlGpu _nvml;
        public GpuWhenWatched(AppController app, NvmlGpu nvml) { _app = app; _nvml = nvml; }
        // The hardware-thread GPU tick does the asking (and backs off when idle); the monitor only reads its result.
        public GpuReading? Read() => _app.GpuDetail ? _app.LastGpu : null;
    }

    /// <summary>EC temperatures and fans through the safety layer's read path.</summary>
    private sealed class EcSensors : IEcSensors
    {
        private readonly EcController _ec;
        public EcSensors(EcController ec) => _ec = ec;
        public EcReading Read() => new(_ec.TryGet("cpuTemp"), _ec.TryGet("cpuFan"), _ec.TryGet("gpuTemp"), _ec.TryGet("gpuFan"),
            FanRpm.FromTach(_ec.TryGet("cpuFanTachLo"), _ec.TryGet("cpuFanTachHi")),
            FanRpm.FromTach(_ec.TryGet("gpuFanTachLo"), _ec.TryGet("gpuFanTachHi")));
    }
}

/// <summary>Names of the monitored metrics, shared by the history and the Monitor window.</summary>
public static class MetricKeys
{
    public static readonly string[] All =
    {
        "cpuTemp", "cpuW", "cpuMhz", "cpuLoad", "cpuFan", "gpuTemp", "gpuFan", "gpuMhz", "gpuLoad", "gpuW", "batteryW", "ramGb",
    };

    public static IEnumerable<(string Key, double? Value)> Values(MonitorSample s)
    {
        yield return ("cpuTemp", s.CpuTemp is > 0 ? s.CpuTemp : null);
        yield return ("cpuW", s.CpuW);
        yield return ("cpuMhz", s.CpuMhz);
        yield return ("cpuLoad", s.CpuLoad);
        yield return ("cpuFan", s.CpuFan is int cf ? FanPercent.Of(cf) : null);
        yield return ("gpuTemp", s.GpuTemp is > 0 ? s.GpuTemp : null);
        yield return ("gpuFan", s.GpuFan is int gf ? FanPercent.Of(gf) : null);
        yield return ("gpuMhz", s.GpuMhz);
        yield return ("gpuLoad", s.GpuLoad);
        yield return ("gpuW", s.GpuW);
        yield return ("batteryW", s.BatteryW);
        yield return ("ramGb", s.RamUsedGb);
    }
}
