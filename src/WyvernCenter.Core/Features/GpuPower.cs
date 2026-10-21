using WyvernCenter.Core.Monitoring;

namespace WyvernCenter.Core.Features;

/// <summary>
/// Software GPU power cap (NVIDIA blocks the real one on this laptop): once a second, moves a clock cap so the GPU's
/// measured watts stay at or under the target, never above the profile's own max clock. Hardware thread only.
/// </summary>
public sealed class GpuPowerLimiter
{
    public const int StepMhz = 50, BigStepMhz = 150, FloorMhz = 600;
    public const double OverW = 0.5, FarOverW = 5, UnderW = 2;

    /// <summary>The limiter's own cap (MHz); null when no power limit is active.</summary>
    public int? Cap { get; private set; }

    public void Reset() => Cap = null;

    /// <returns>The cap to write (null = no cap).</returns>
    /// <param name="clockMhz">The clock the GPU is running now: when over target, stepping starts from it, so a cap
    /// above the real clock doesn't waste seconds stepping down to it.</param>
    public int? Step(int? targetW, int? ceilingMhz, double? watts, double? clockMhz = null)
    {
        if (targetW is not int t) { Cap = null; return ceilingMhz; }
        var ceiling = ceilingMhz ?? Presets.GpuTopMhz;
        var cap = Cap ?? ceiling;
        if (watts is double w)
        {
            if (w > t + OverW && clockMhz is double mhz and > 0) cap = Math.Min(cap, (int)(mhz / StepMhz) * StepMhz);
            if (w > t + OverW) cap -= w > t + FarOverW ? BigStepMhz : StepMhz;
            else if (w < t - UnderW) cap += StepMhz;
        }
        Cap = Math.Clamp(cap, FloorMhz, ceiling);
        return Normalise(Cap.Value);
    }

    /// <summary>The cap that should be on the GPU now: the limiter's (when limiting) within the profile's max clock.</summary>
    public int? Effective(int? ceilingMhz, int? targetW)
    {
        if (targetW is null || Cap is not int c) return ceilingMhz;
        return Normalise(Math.Min(c, ceilingMhz ?? Presets.GpuTopMhz));
    }

    private static int? Normalise(int cap) => cap >= Presets.GpuTopMhz ? null : cap;
}

/// <summary>
/// When to ask the GPU anything. Asking keeps an idle GPU awake, so: never while it sleeps, and after a few idle
/// readings stop until it gets warmer (real work) or has slept and woken again. Hardware thread only.
/// </summary>
public sealed class GpuPollPolicy
{
    public const int IdleReadingsBeforeBackOff = 3, ResumeRiseC = 3;
    /// <summary>Idle = no load. Not judged by watts: this GPU can sit in P0 at ~15 W doing nothing.</summary>
    public const double IdleLoadPct = 3;
    private int _idle;
    private int? _backedOffAt;

    public bool BackedOff => _backedOffAt is not null;

    public bool ShouldPoll(int? ecTemp)
    {
        if (ecTemp is not int t || t <= 0)
        {
            _idle = 0;
            _backedOffAt = null;
            return false;
        }
        if (_backedOffAt is not int at) return true;
        if (t < at) _backedOffAt = at = t; // cooled while idle: measure the rise from the coolest point
        if (t < at + ResumeRiseC) return false;
        _backedOffAt = null;
        _idle = 0;
        return true;
    }

    public void Report(GpuReading? reading, int? ecTemp)
    {
        var idle = reading is { } r && (r.Load ?? 0) < IdleLoadPct;
        _idle = idle ? _idle + 1 : 0;
        if (_idle >= IdleReadingsBeforeBackOff && ecTemp is int t and > 0) _backedOffAt = t;
    }
}

/// <summary>The NVIDIA readings source (NVML). Release closes the session so an idle GPU can sleep.</summary>
public interface IGpuReader
{
    GpuReading? Read();
    void Release();
}

/// <summary>
/// The once-a-second GPU job: decides whether to ask the GPU anything (poll policy), steps the power limiter, and
/// always closes the NVML session when it isn't actively asking, so nothing it does keeps an idle GPU awake.
/// Hardware thread only; <see cref="Last"/> may be read from any thread.
/// </summary>
public sealed class GpuWatcher
{
    private sealed record Box(GpuReading? Reading);
    private readonly IGpuReader _gpu;
    private volatile Box? _last;
    private volatile bool _polling;

    public GpuWatcher(IGpuReader gpu) => _gpu = gpu;

    public GpuPollPolicy Policy { get; } = new();
    public GpuPowerLimiter Limiter { get; } = new();
    /// <summary>True while an NVML session is open because the GPU is being asked for readings.</summary>
    public bool Polling => _polling;
    /// <summary>The latest reading; null while not asking.</summary>
    public GpuReading? Last => _last?.Reading;

    /// <param name="needed">The Monitor window is open, or the profile in use has a power limit the GPU can follow.</param>
    /// <returns>True when the limiter moved its cap (the caller writes the new effective cap).</returns>
    public bool Tick(bool needed, int? ecTemp, int? targetW, int? ceilingMhz)
    {
        if (!needed)
        {
            Stop(); // also closes a session opened by a clock write elsewhere; a no-op when nothing is open
            return false;
        }
        if (!Policy.ShouldPoll(ecTemp))
        {
            Stop();
            return false;
        }
        _polling = true;
        var reading = _gpu.Read();
        Policy.Report(reading, ecTemp);
        _last = new Box(reading);
        var moved = false;
        if (targetW is not null && reading?.Watts is double w)
        {
            var before = Limiter.Cap;
            Limiter.Step(targetW, ceilingMhz, w, reading?.Mhz);
            moved = Limiter.Cap != before;
        }
        if (reading is null || Policy.BackedOff)
        {
            _polling = false; // a failed start is retried next tick; backed off = let it sleep
            _gpu.Release();
        }
        return moved;
    }

    private void Stop()
    {
        _polling = false;
        _last = null;
        _gpu.Release();
    }
}
