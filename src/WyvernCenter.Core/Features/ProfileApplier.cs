using WyvernCenter.Core.Safety;

namespace WyvernCenter.Core.Features;

/// <summary>How a clock cap is put into Windows' power plan (spec §3).</summary>
public static class CpuClock
{
    public sealed record Plan(int FreqMaxMhz, int ThrottleMaxPercent);

    /// <param name="frequencyCapWorks">False if the hardware check showed PROCFREQMAX isn't honoured: caps below base
    /// then use max processor state (turbo off), caps above base can't be expressed and mean no limit.</param>
    public static Plan For(int? mhz, bool frequencyCapWorks)
    {
        if (frequencyCapWorks) return new Plan(mhz ?? 0, 100);
        if (mhz is int m && m < Presets.BaseMhz) return new Plan(0, (int)Math.Round(m * 100.0 / Presets.BaseMhz));
        return new Plan(0, 100);
    }
}

/// <summary>
/// Writes a whole profile: firmware level, both fan curves then advanced fan mode, turbo, clock cap, Windows power
/// mode. A failed step is reported and the rest still run; a bad curve stops every fan write.
/// </summary>
public sealed class ProfileApplier
{
    private readonly EcController _ec;
    private readonly ICpuPower _cpu;
    private readonly IPowerOverlay? _overlay;
    private readonly bool _frequencyCapWorks;

    public ProfileApplier(EcController ec, ICpuPower cpu, IPowerOverlay? overlay, bool frequencyCapWorks = true)
    {
        _ec = ec;
        _cpu = cpu;
        _overlay = overlay;
        _frequencyCapWorks = frequencyCapWorks;
    }

    /// <param name="skipFans">True while the thermal failsafe owns the fans.</param>
    public IReadOnlyList<SetResult> Apply(Profile p, bool skipFans = false)
    {
        var results = new List<SetResult> { SetNamed("shiftMode", Profile.ShiftFor(p.Firmware)) };
        if (!skipFans) results.AddRange(FanControl.ApplyCurves(_ec, p.CpuCurve, p.GpuCurve)); // nothing on unconfirmed models
        // Fail closed: without the originals saved, Restore couldn't undo a (hidden) clock cap.
        try { _cpu.SaveOriginalsIfMissing(); }
        catch (Exception ex)
        {
            results.Add(new SetResult("cpu", SetOutcome.Failed, "CPU settings not changed: couldn't save the originals (" + ex.Message + ")"));
            if (_overlay is not null) results.Add(Step("powerMode", () => { _overlay.Set(p.Windows); return p.Windows.ToString(); }));
            return results;
        }
        results.Add(Step("cpuBoost", () => { _cpu.SetBoost(Profile.BoostFor(p.Turbo)); return p.Turbo.ToString(); }));
        var plan = CpuClock.For(p.MaxClockMhz, _frequencyCapWorks);
        results.Add(Step("maxClock", () =>
        {
            _cpu.SetMaxFrequency(plan.FreqMaxMhz);
            _cpu.SetMaxState(true, plan.ThrottleMaxPercent);
            _cpu.SetMaxState(false, plan.ThrottleMaxPercent);
            return plan.FreqMaxMhz == 0 ? "no limit" : plan.FreqMaxMhz + " MHz";
        }));
        if (_overlay is not null) results.Add(Step("powerMode", () => { _overlay.Set(p.Windows); return p.Windows.ToString(); }));
        return results;
    }

    private static SetResult Step(string name, Func<string> action)
    {
        try { return new SetResult(name, SetOutcome.Ok, action()); }
        catch (Exception ex) { return new SetResult(name, SetOutcome.Failed, ex.Message); }
    }

    private SetResult SetNamed(string setting, string valueName)
    {
        var values = _ec.Profile?.Settings.GetValueOrDefault(setting)?.Values;
        return values is not null && values.TryGetValue(valueName, out var value)
            ? _ec.Set(setting, value)
            : new SetResult(setting, SetOutcome.Rejected, $"{valueName} not defined for this model");
    }
}
