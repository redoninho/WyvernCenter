using WyvernCenter.Core.Monitoring;

namespace WyvernCenter.Core.Verify;

/// <summary>One phase's averages under load (null where the sensor gave nothing usable).</summary>
public sealed record LoadReading(double? Watts, double? Mhz, int? MaxTempC);

public enum MeasureEvent { None, SwitchNow, Finished, Stopped }

/// <summary>
/// The measured MSI-level step. A level only changes the firmware's power limits, so at idle nothing visible
/// happens; under full CPU load the limits show as power and clock. Phase 1 loads the CPU in the current level,
/// phase 2 in the test level; each averages CPU power and clock after a short warm-up. Fed with monitor samples on
/// one thread: the caller switches the level on <see cref="MeasureEvent.SwitchNow"/> and stops the load on
/// Finished/Stopped. It ends exactly once.
/// </summary>
public sealed class ModeMeasurement
{
    public const int WarmupSeconds = 5, MeasureSeconds = 10, StopAtC = 95, MinSamples = 4;
    public const int PhaseSeconds = WarmupSeconds + MeasureSeconds;
    /// <summary>The app stops the load at this point no matter what, so it can't outlive stalled samples.</summary>
    public const int DeadlineSeconds = 2 * PhaseSeconds + 20;

    private readonly List<MonitorSample> _steady = new();
    private DateTime _phaseStart;
    private bool _waitingForSwitch;

    public ModeMeasurement(DateTime start) => _phaseStart = start;

    /// <summary>1 = current level, 2 = test level, 3 = ended.</summary>
    public int Phase { get; private set; } = 1;
    public LoadReading? Before { get; private set; }
    public LoadReading? After { get; private set; }
    public string? StopReason { get; private set; }

    public MeasureEvent Sample(MonitorSample s, DateTime now, bool failsafe)
    {
        if (Phase == 3 || _waitingForSwitch) return MeasureEvent.None;
        if (failsafe || s.CpuTemp is >= StopAtC and < 120)
        {
            Stop(failsafe ? "the thermal failsafe engaged" : $"the CPU reached {s.CpuTemp} °C");
            return MeasureEvent.Stopped;
        }
        var elapsed = (now - _phaseStart).TotalSeconds;
        if (elapsed >= WarmupSeconds) _steady.Add(s);
        if (elapsed < PhaseSeconds) return MeasureEvent.None;
        var reading = Average(_steady);
        _steady.Clear();
        if (Phase == 1)
        {
            Before = reading;
            _waitingForSwitch = true;
            return MeasureEvent.SwitchNow;
        }
        After = reading;
        Phase = 3;
        return MeasureEvent.Finished;
    }

    /// <summary>The test level is set: phase 2 starts now.</summary>
    public void Switched(DateTime now)
    {
        if (Phase == 3) return;
        _waitingForSwitch = false;
        Phase = 2;
        _phaseStart = now;
    }

    public void Stop(string reason)
    {
        if (Phase == 3) return;
        Phase = 3;
        StopReason = reason;
    }

    public string Progress(DateTime now, MonitorSample? last, string levelName)
    {
        var elapsed = Math.Min(PhaseSeconds, (now - _phaseStart).TotalSeconds);
        var parts = new List<string> { $"{levelName}: {(elapsed < WarmupSeconds ? "warming up" : "measuring")} {elapsed:F0} / {PhaseSeconds} s" };
        if (last?.CpuW is double w) parts.Add($"{w:F0} W");
        if (last?.CpuMhz is double mhz) parts.Add($"{mhz / 1000:F1} GHz");
        if (last?.CpuTemp is int t and > 0) parts.Add($"{t} °C");
        return string.Join(" · ", parts);
    }

    public static LoadReading Average(IReadOnlyList<MonitorSample> samples)
    {
        double? Avg(IEnumerable<double?> xs)
        {
            var v = xs.Where(x => x is > 0).Select(x => x!.Value).ToList();
            return v.Count >= MinSamples ? v.Average() : null;
        }
        var temps = samples.Where(s => s.CpuTemp is > 0 and < 120).Select(s => s.CpuTemp!.Value).ToList();
        return new LoadReading(Avg(samples.Select(s => s.CpuW)), Avg(samples.Select(s => s.CpuMhz)), temps.Count > 0 ? temps.Max() : null);
    }

    /// <summary>
    /// Yes when CPU power (15 %) or clock (10 %) clearly differs between the two levels, No when they were measured
    /// and didn't, Skipped when there weren't enough readings to say.
    /// </summary>
    public static (StepAnswer Answer, string Summary) Verdict(LoadReading? before, LoadReading? after, string fromName, string toName)
    {
        if (before is null || after is null) return (StepAnswer.Skipped, "couldn't measure");
        var summary = $"{fromName} {Show(before)} → {toName} {Show(after)}";
        bool Both(double? a, double? b) => a is not null && b is not null;
        bool Differs(double? a, double? b, double share) => Both(a, b) && Math.Abs(b!.Value - a!.Value) / a.Value >= share;
        if (!Both(before.Watts, after.Watts) && !Both(before.Mhz, after.Mhz)) return (StepAnswer.Skipped, "not enough readings: " + summary);
        return Differs(before.Watts, after.Watts, 0.15) || Differs(before.Mhz, after.Mhz, 0.10)
            ? (StepAnswer.Yes, "clear difference: " + summary)
            : (StepAnswer.No, "no clear difference: " + summary);
    }

    public static string Show(LoadReading r)
    {
        var parts = new List<string>();
        if (r.Watts is double w) parts.Add($"{w:F0} W");
        if (r.Mhz is double mhz) parts.Add($"{mhz / 1000:F1} GHz");
        if (r.MaxTempC is int t) parts.Add($"max {t} °C");
        return parts.Count == 0 ? "(no readings)" : string.Join(", ", parts);
    }
}
