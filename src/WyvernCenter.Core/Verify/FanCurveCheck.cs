namespace WyvernCenter.Core.Verify;

public enum FanCheckEvent { None, TryCandidate, PutModeBack, Finished }

/// <param name="Value">The fan-mode byte tried.</param>
/// <param name="BeforeRpm">CPU fan speed with nothing (or only the raised curves) applied.</param>
/// <param name="PeakRpm">The fastest reading while it was on; null when it couldn't be written.</param>
public sealed record FanTry(int Value, double? BeforeRpm, int? PeakRpm);

/// <summary>
/// The fan-curve check on WMI v2 laptops. Which fan-mode byte turns custom curves on differs between MSI Center's own
/// code (bit 6: 0x4D) and msi-ec (bit 7: 0x8D), and a wrong guess leaves the fans on the firmware's own table. So
/// Wyvern measures it: first the fans as they are, then both curves raised to their own top speed (faster fans are
/// the safe direction) with one candidate value, watching the CPU fan. A candidate counts when the fan clearly speeds
/// up; otherwise the mode goes back, the fans settle, and the next candidate is tried. Fed with fan readings on one
/// thread; the caller makes the writes it is told to and reports back.
/// </summary>
public sealed class FanCurveCheck
{
    public const int BaselineSeconds = 6, WatchSeconds = 15, SettleSeconds = 10, MinReadings = 3;
    /// <summary>Clearly faster: at least 15 % and 300 RPM above the speed before.</summary>
    public const double FasterShare = 0.15;
    public const int FasterRpm = 300;

    public const int MsiValue = 0x4D, MsiEcValue = 0x8D;

    public enum Stage { Baseline, Watching, Settling, Done }

    private readonly IReadOnlyList<int> _candidates;
    private readonly List<int> _window = new();
    private DateTime _since;
    private bool _waiting;
    private double? _before;

    public FanCurveCheck(IReadOnlyList<int> candidates, DateTime start)
    {
        _candidates = candidates;
        _since = start;
    }

    public Stage Now { get; private set; } = Stage.Baseline;
    public int Candidate { get; private set; }
    public int CandidateValue => _candidates[Candidate];
    public bool FirstCandidate => Candidate == 0;
    public List<FanTry> Tried { get; } = new();
    public int? Confirmed { get; private set; }
    public string? StopReason { get; private set; }
    public int? LastRpm { get; private set; }

    /// <summary>The order to try: the one the fan-mode byte already uses (a report taken with MSI Center's custom
    /// fan mode on), else MSI Center's own encoding first.</summary>
    public static IReadOnlyList<int> Candidates(int currentFanMode) =>
        (currentFanMode & 0xC0) == 0x80 ? new[] { MsiEcValue, MsiValue } : new[] { MsiValue, MsiEcValue };

    public static bool Faster(double before, int peak) => peak >= before * (1 + FasterShare) && peak >= before + FasterRpm;

    public FanCheckEvent Sample(int? rpm, DateTime now)
    {
        if (Now == Stage.Done || _waiting) return FanCheckEvent.None;
        if (rpm is int r and > 0)
        {
            _window.Add(r);
            LastRpm = r;
        }
        var elapsed = (now - _since).TotalSeconds;
        switch (Now)
        {
            case Stage.Baseline or Stage.Settling:
                if (elapsed < (Now == Stage.Baseline ? BaselineSeconds : SettleSeconds)) return FanCheckEvent.None;
                if (_window.Count < MinReadings) return Stop("no fan speed readings");
                _before = _window.TakeLast(MinReadings + 1).Average(); // the settled end of the window
                _waiting = true;
                return FanCheckEvent.TryCandidate;
            case Stage.Watching:
                var peak = _window.Count > 0 ? _window.Max() : (int?)null;
                if (peak is int p && Faster(_before!.Value, p))
                {
                    Tried.Add(new FanTry(CandidateValue, _before, p));
                    Confirmed = CandidateValue;
                    Now = Stage.Done;
                    return FanCheckEvent.Finished;
                }
                if (elapsed < WatchSeconds) return FanCheckEvent.None;
                Tried.Add(new FanTry(CandidateValue, _before, peak));
                return Next();
        }
        return FanCheckEvent.None;
    }

    /// <summary>The caller wrote the candidate (and, the first time, the raised curves).</summary>
    public FanCheckEvent Applied(DateTime now, bool ok)
    {
        _waiting = false;
        if (Now == Stage.Done) return FanCheckEvent.None;
        if (!ok)
        {
            Tried.Add(new FanTry(CandidateValue, _before, null));
            return Next();
        }
        Now = Stage.Watching;
        _window.Clear();
        _since = now;
        return FanCheckEvent.None;
    }

    /// <summary>The caller put the fan mode back after a candidate that didn't work: settle, then try the next.</summary>
    public void ModeBack(DateTime now)
    {
        _waiting = false;
        if (Now == Stage.Done) return;
        Candidate++;
        Now = Stage.Settling;
        _window.Clear();
        _since = now;
    }

    public FanCheckEvent Stop(string reason)
    {
        if (Now == Stage.Done) return FanCheckEvent.None;
        StopReason = reason;
        Now = Stage.Done;
        return FanCheckEvent.Finished;
    }

    private FanCheckEvent Next()
    {
        if (Candidate + 1 >= _candidates.Count)
        {
            Now = Stage.Done;
            return FanCheckEvent.Finished;
        }
        _waiting = true;
        return FanCheckEvent.PutModeBack;
    }

    public string Progress(DateTime now)
    {
        var elapsed = (now - _since).TotalSeconds;
        var rpm = LastRpm is int r ? $" · CPU fan {r} RPM" : "";
        return Now switch
        {
            Stage.Baseline => $"Measuring your fans as they are: {Math.Min(elapsed, BaselineSeconds):F0} / {BaselineSeconds} s{rpm}",
            Stage.Watching => $"Trying {Name(CandidateValue)}: {Math.Min(elapsed, WatchSeconds):F0} / {WatchSeconds} s{rpm}",
            Stage.Settling => $"Letting the fans settle: {Math.Min(elapsed, SettleSeconds):F0} / {SettleSeconds} s{rpm}",
            _ => "Putting your fans back…",
        };
    }

    public static string Name(int value) => value switch
    {
        MsiValue => "0x4D (MSI Center's way)",
        MsiEcValue => "0x8D (msi-ec's way)",
        _ => $"0x{value:X2}",
    };

    /// <summary>Yes = custom curves work with <see cref="Confirmed"/>; No = neither sped the fans up; Skipped = couldn't run.</summary>
    public (StepAnswer Answer, string Summary) Verdict()
    {
        string Tries() => string.Join("; ", Tried.Select(t =>
            $"{Name(t.Value)}: {(t.PeakRpm is int p ? $"{t.BeforeRpm:F0} → {p} RPM" : "couldn't be written")}"));
        if (StopReason is not null) return (StepAnswer.Skipped, "not finished: " + StopReason + (Tried.Count > 0 ? "; " + Tries() : ""));
        if (Confirmed is int c) return (StepAnswer.Yes, $"custom curves work with fan mode 0x{c:X2}: {Tries()}");
        return (StepAnswer.No, "neither value sped the fans up (if they were already loud, try again when the laptop is cool): " + Tries());
    }
}
