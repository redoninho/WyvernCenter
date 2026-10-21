using WyvernCenter.Core.Profile;

namespace WyvernCenter.Core.Features;

/// <summary>How eagerly the CPU goes above its base clock (Windows boost mode).</summary>
public enum Turbo { Off, Efficient, Aggressive }

/// <summary>MSI's firmware power level (EC shift mode): its own internal power limits.</summary>
public enum FirmwareLevel { SuperBattery, Normal, Extreme }

/// <summary>The EC reports fan speed on a 0–150 scale (150 = full speed, seen at 95 °C); the UI shows percent.</summary>
public static class FanPercent
{
    public const int Max = 150;
    public static int Of(int raw) => (int)Math.Round(Math.Clamp(raw, 0, Max) * 100.0 / Max);
}

/// <summary>A complete setup: CPU clock and turbo, MSI firmware level, Windows power mode and both fan curves.</summary>
public sealed class Profile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool BuiltIn { get; set; }
    /// <summary>CPU clock cap in MHz; null = no limit.</summary>
    public int? MaxClockMhz { get; set; }
    public Turbo Turbo { get; set; } = Turbo.Efficient;
    public FirmwareLevel Firmware { get; set; } = FirmwareLevel.Normal;
    public PowerOverlay Windows { get; set; } = PowerOverlay.Balanced;
    /// <summary>Six points on the EC's 0–150 scale at the firmware's CPU temperature thresholds.</summary>
    public int[] CpuCurve { get; set; } = new int[6];
    public int[] GpuCurve { get; set; } = new int[6];
    /// <summary>GPU core clock cap in MHz; null = no cap.</summary>
    public int? GpuMaxClockMhz { get; set; }
    /// <summary>Software GPU power cap in watts (the limiter lowers the clock to stay under it); null = no limit.</summary>
    public int? GpuPowerW { get; set; }
    /// <summary>Whether the profile hotkey steps through this profile; null = the preset's default (custom: no).</summary>
    public bool? InCycle { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsInCycle => InCycle ?? Presets.DefaultInCycle(Id);

    public Profile Clone() => new()
    {
        Id = Id, Name = Name, BuiltIn = BuiltIn, MaxClockMhz = MaxClockMhz, Turbo = Turbo, Firmware = Firmware, Windows = Windows,
        CpuCurve = (int[])CpuCurve.Clone(), GpuCurve = (int[])GpuCurve.Clone(),
        GpuMaxClockMhz = GpuMaxClockMhz, GpuPowerW = GpuPowerW, InCycle = InCycle,
    };

    public static CpuBoost BoostFor(Turbo t) => t switch { Turbo.Off => CpuBoost.Off, Turbo.Efficient => CpuBoost.Efficient, _ => CpuBoost.Aggressive };

    /// <summary>The model profile's value name for <c>shiftMode</c>.</summary>
    public static string ShiftFor(FirmwareLevel f) => f switch { FirmwareLevel.SuperBattery => "eco", FirmwareLevel.Extreme => "turbo", _ => "comfort" };
}

/// <summary>Our shipped profiles (spec §5). Each call to <see cref="Create"/> returns a fresh copy.</summary>
public static class Presets
{
    public const string Battery = "battery", Quiet = "quiet", Balanced = "balanced", Performance = "performance", Extreme = "extreme";

    /// <summary>This machine's CPU and GPU ranges, set once at start-up from detection (see <see cref="HardwareLimits"/>).</summary>
    public static HardwareLimits Hardware { get; set; } = HardwareLimits.Fallback;

    public static int MinMhz => Hardware.CpuMinMhz;
    public static int BaseMhz => Hardware.CpuBaseMhz;
    public static int MaxMhz => Hardware.CpuMaxMhz;
    /// <summary>GPU clock cap range (50 MHz steps); the GPU's own top speed is <see cref="GpuTopMhz"/>.</summary>
    public static int GpuMinMhz => Hardware.GpuMinMhz;
    public static int GpuCapMaxMhz => Hardware.GpuCapMaxMhz;
    public static int GpuTopMhz => Hardware.GpuTopMhz;
    /// <summary>GPU power cap range in watts.</summary>
    public static int GpuMinW => Hardware.GpuMinW;
    public static int GpuMaxW => Hardware.GpuMaxW;
    /// <summary>How many profiles of their own the user can make.</summary>
    public const int MaxOwnProfiles = 2; // 5 presets + 2 own + New/Edit = two rows of tiles
    public static readonly IReadOnlyList<string> Ids = new[] { Battery, Quiet, Balanced, Performance, Extreme };

    /// <summary>Percent of full fan speed → the EC's 0–150 scale.</summary>
    public static int[] Curve(params int[] pct) => pct.Select(p => (int)Math.Round(p * FanPercent.Max / 100.0, MidpointRounding.AwayFromZero)).ToArray();

    public static Profile Create(string id) => Create(id, Hardware);

    /// <summary>
    /// The preset scaled to this hardware: clock caps as fractions of base / turbo range / GPU top speed, power as a
    /// fraction of the GPU's own limit (on the GF63 these give 2.0/2.5/3.5 GHz, 900/1100/1350/1600 MHz, 15/20/25/32 W).
    /// </summary>
    public static Profile Create(string id, HardwareLimits hw)
    {
        var p = id switch
        {
            Battery => Make("Battery", hw.CpuBelowBase(0.8), Turbo.Off, FirmwareLevel.SuperBattery, PowerOverlay.Efficiency, hw.GpuClock(0.52), hw.GpuWatts(0.375), 15, 20, 30, 45, 60, 75),
            Quiet => Make("Quiet", hw.CpuBaseMhz, Turbo.Off, FirmwareLevel.Normal, PowerOverlay.Efficiency, hw.GpuClock(0.63), hw.GpuWatts(0.5), 20, 25, 35, 50, 65, 80),
            Balanced => Make("Balanced", hw.CpuAt(0.5), Turbo.Efficient, FirmwareLevel.Normal, PowerOverlay.Balanced, hw.GpuClock(0.78), hw.GpuWatts(0.625), 25, 40, 55, 70, 85, 100),
            Performance => Make("Performance", null, Turbo.Aggressive, FirmwareLevel.Normal, PowerOverlay.Performance, hw.GpuClock(0.92), hw.GpuWatts(0.8), 30, 55, 70, 85, 95, 100),
            Extreme => Make("Extreme", null, Turbo.Aggressive, FirmwareLevel.Extreme, PowerOverlay.Performance, null, null, 40, 70, 85, 100, 100, 100),
            _ => throw new ArgumentException("Unknown preset " + id, nameof(id)),
        };
        p.Id = id;
        p.BuiltIn = true;
        return p;
    }

    /// <summary>Every profile is in the hotkey cycle unless the user unticks it.</summary>
    public static bool DefaultInCycle(string id) => true;

    private static Profile Make(string name, int? mhz, Turbo turbo, FirmwareLevel fw, PowerOverlay win, int? gpuMhz, int? gpuW, params int[] fanPct) => new()
    {
        Name = name, MaxClockMhz = mhz, Turbo = turbo, Firmware = fw, Windows = win, GpuMaxClockMhz = gpuMhz, GpuPowerW = gpuW,
        CpuCurve = Curve(fanPct), GpuCurve = Curve(fanPct),
    };
}

/// <summary>
/// The list of profiles and the rules for changing it (spec §4). Presets are always present and first, in preset
/// order. Not thread-safe: used from the UI thread.
/// </summary>
public sealed class ProfileStore
{
    private readonly List<Profile> _profiles;
    private readonly CurveRules? _rules;
    private readonly HardwareLimits _hw;

    public ProfileStore(IEnumerable<Profile>? saved, string? activeId, string? onAc, string? onBattery, CurveRules? rules,
        HardwareLimits? hardware = null)
    {
        _rules = rules;
        _hw = hardware ?? Presets.Hardware;
        var list = (saved ?? Enumerable.Empty<Profile>()).Where(p => p is not null && p.Id != "").ToList();
        _profiles = Presets.Ids.Select(id => list.FirstOrDefault(p => p.Id == id) is { } kept ? Normalised(kept, builtIn: true) : Presets.Create(id, _hw)).ToList();
        _profiles.AddRange(list.Where(p => !Presets.Ids.Contains(p.Id)).Select(p => Normalised(p, builtIn: false)));
        ActiveId = activeId is not null && Find(activeId) is not null ? activeId : null;
        OnAc = onAc is not null && Find(onAc) is not null ? onAc : Presets.Balanced;
        OnBattery = onBattery is not null && Find(onBattery) is not null ? onBattery : Presets.Battery;
    }

    public IReadOnlyList<Profile> Profiles => _profiles;
    /// <summary>The profile in use; null until the user picks one (nothing is written before that).</summary>
    public string? ActiveId { get; set; }
    public Profile? Active => ActiveId is null ? null : Find(ActiveId);
    public string OnAc { get; set; }
    public string OnBattery { get; set; }

    public Profile? Find(string id) => _profiles.FirstOrDefault(p => p.Id == id);
    public Profile Get(string id) => Find(id) ?? throw new KeyNotFoundException("No profile " + id);

    public Profile New(string fromId)
    {
        var n = 1;
        while (NameTaken($"My profile {n}")) n++;
        return AddCopy(fromId, $"My profile {n}");
    }

    public Profile Duplicate(string id)
    {
        var baseName = Get(id).Name + " copy";
        var name = baseName;
        for (var n = 2; NameTaken(name); n++) name = $"{baseName} {n}";
        return AddCopy(id, name);
    }

    public void Rename(string id, string name)
    {
        var p = Get(id);
        if (p.BuiltIn) throw new InvalidOperationException("Presets can't be renamed; duplicate it instead.");
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("The name can't be empty.");
        if (name.Length > 40) throw new ArgumentException("Keep the name under 40 characters.");
        if (_profiles.Any(o => o.Id != id && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There's already a profile called {name}.");
        p.Name = name;
    }

    public void Delete(string id)
    {
        var p = Get(id);
        if (p.BuiltIn) throw new InvalidOperationException("Presets can't be deleted.");
        _profiles.Remove(p);
        if (ActiveId == id) ActiveId = Presets.Balanced;
        if (OnAc == id) OnAc = Presets.Balanced;
        if (OnBattery == id) OnBattery = Presets.Battery;
    }

    public void Reset(string id)
    {
        var i = _profiles.FindIndex(p => p.Id == id);
        if (i < 0) throw new KeyNotFoundException("No profile " + id);
        if (!_profiles[i].BuiltIn) throw new InvalidOperationException("Only presets can be reset.");
        _profiles[i] = Presets.Create(id, _hw);
    }

    /// <summary>Applies a change to a copy, normalises and validates it; stores it only when valid.</summary>
    /// <returns>Validation errors; empty when the change was stored.</returns>
    public IReadOnlyList<string> Edit(string id, Action<Profile> change)
    {
        var i = _profiles.FindIndex(p => p.Id == id);
        if (i < 0) throw new KeyNotFoundException("No profile " + id);
        var copy = _profiles[i].Clone();
        change(copy);
        copy.Id = _profiles[i].Id;
        copy.BuiltIn = _profiles[i].BuiltIn;
        if (copy.Turbo == Turbo.Off && (copy.MaxClockMhz is null || copy.MaxClockMhz > _hw.CpuBaseMhz)) copy.MaxClockMhz = _hw.CpuBaseMhz;
        var errors = Validate(copy, _rules, _hw);
        if (errors.Count > 0) return errors;
        _profiles[i] = copy;
        return errors;
    }

    /// <summary>The profile the hotkey switches to next: the one after the current in list order (wrapping), or the
    /// first in the cycle when the current isn't in it. Null when no profile is in the cycle.</summary>
    public string? NextInCycle(string? currentId)
    {
        var cycle = _profiles.Where(p => p.IsInCycle).Select(p => p.Id).ToList();
        if (cycle.Count == 0) return null;
        var i = currentId is null ? -1 : cycle.IndexOf(currentId);
        return cycle[(i + 1) % cycle.Count];
    }

    /// <summary>Hotkey membership only: not a change to what the profile does.</summary>
    public void SetInCycle(string id, bool on) => Get(id).InCycle = on;

    public static IReadOnlyList<string> Validate(Profile p, CurveRules? rules, HardwareLimits? hardware = null)
    {
        var hw = hardware ?? Presets.Hardware;
        var errors = new List<string>();
        if (p.MaxClockMhz is int mhz && (mhz < hw.CpuMinMhz || mhz > hw.CpuMaxMhz || mhz % 100 != 0))
            errors.Add($"Max clock must be {hw.CpuMinMhz / 1000.0:F1}–{hw.CpuMaxMhz / 1000.0:F1} GHz in 0.1 GHz steps.");
        if (p.GpuMaxClockMhz is int g && (g < hw.GpuMinMhz || g > hw.GpuCapMaxMhz || g % 50 != 0))
            errors.Add($"GPU max clock must be {hw.GpuMinMhz}–{hw.GpuCapMaxMhz} MHz in 50 MHz steps, or no cap.");
        if (p.GpuPowerW is int gw && (gw < hw.GpuMinW || gw > hw.GpuMaxW))
            errors.Add($"GPU power must be {hw.GpuMinW}–{hw.GpuMaxW} W, or no limit.");
        if (rules is not null)
        {
            errors.AddRange(FanCurveRules.Validate(p.CpuCurve, rules).Select(e => "CPU fan: " + e));
            errors.AddRange(FanCurveRules.Validate(p.GpuCurve, rules).Select(e => "GPU fan: " + e));
        }
        return errors;
    }

    /// <summary>Maps v0.2.0 mode names to preset ids.</summary>
    public static (string? Active, string OnAc, string OnBattery) Migrate(string? mode, string? modeOnAc, string? modeOnBattery)
    {
        static string? Map(string? m, string silent) => m switch
        {
            "Silent" => silent,
            "Balanced" => Presets.Balanced,
            "Performance" => Presets.Performance,
            _ => null,
        };
        return (Map(mode, Presets.Quiet), Map(modeOnAc, Presets.Quiet) ?? Presets.Balanced, Map(modeOnBattery, Presets.Battery) ?? Presets.Battery);
    }

    private bool NameTaken(string name) => _profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>False once the user has <see cref="Presets.MaxOwnProfiles"/> profiles of their own.</summary>
    public bool CanAddOwn => _profiles.Count(p => !p.BuiltIn) < Presets.MaxOwnProfiles;

    private Profile AddCopy(string fromId, string name)
    {
        if (!CanAddOwn) throw new InvalidOperationException($"Up to {Presets.MaxOwnProfiles} of your own profiles; delete one to make another.");
        var p = Get(fromId).Clone();
        p.Id = Guid.NewGuid().ToString("N");
        p.Name = name;
        p.BuiltIn = false;
        p.InCycle = true;
        _profiles.Add(p);
        return p;
    }

    /// <summary>Saved profiles may come from an older or hand-edited file: fix shapes so the rest of the app can trust them.</summary>
    private Profile Normalised(Profile p, bool builtIn)
    {
        var fresh = builtIn ? Presets.Create(p.Id, _hw) : null;
        p.BuiltIn = builtIn;
        if (builtIn) p.Name = fresh!.Name;
        if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Profile";
        if (p.CpuCurve is not { Length: 6 }) p.CpuCurve = (fresh ?? Presets.Create(Presets.Balanced, _hw)).CpuCurve;
        if (p.GpuCurve is not { Length: 6 }) p.GpuCurve = (fresh ?? Presets.Create(Presets.Balanced, _hw)).GpuCurve;
        // Saved on (or copied from) other hardware: fit this machine's ranges. A cap at or past the top = no cap.
        if (p.MaxClockMhz is int mhz) p.MaxClockMhz = mhz >= _hw.CpuMaxMhz ? null : Math.Max(_hw.CpuMinMhz, mhz / 100 * 100);
        // GPU caps are only fitted against limits actually read: with no GPU this start (driver mid-update, NVML
        // not ready) they're kept as they are, and simply not applied.
        if (_hw.HasGpu && p.GpuMaxClockMhz is int g) p.GpuMaxClockMhz = g > _hw.GpuCapMaxMhz ? null : Math.Max(_hw.GpuMinMhz, g / 50 * 50);
        if (_hw.HasGpu && p.GpuPowerW is int w) p.GpuPowerW = w > _hw.GpuMaxW ? null : Math.Max(_hw.GpuMinW, w);
        if (p.Turbo == Turbo.Off && (p.MaxClockMhz is null || p.MaxClockMhz > _hw.CpuBaseMhz)) p.MaxClockMhz = _hw.CpuBaseMhz;
        return p;
    }
}
