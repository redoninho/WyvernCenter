using WyvernCenter.Core.Safety;

namespace WyvernCenter.Core.Features;

public sealed record FailsafeRelease(IReadOnlyList<SetResult> Results, bool UserFanPending);

/// <summary>
/// What the thermal failsafe does to the fans. Engage puts both fan curves at maximum through the firmware (local and
/// fast; never through MSI's service: Cooler Boost on top is FailsafeBoost's job, on the hardware queue). Enforce
/// re-asserts that every tick (something else may have overwritten it). Release undoes only what Engage changed, unless the user changed
/// things meanwhile or the state before couldn't be read (then the caller re-applies the profile).
/// Callers serialise access (AppController's failsafe lock).
/// </summary>
public sealed class FailsafeCooling
{
    private readonly EcController _ec;
    private int? _fanModeBefore;
    private int[]? _cpuBefore, _gpuBefore;

    public FailsafeCooling(EcController ec) => _ec = ec;

    public bool Active { get; private set; }
    /// <summary>This model's fan curves can be written, so the failsafe can force full speed (false on unconfirmed models).</summary>
    public bool CanMaxFans => CurvesWritable;
    private bool _movedToAuto;
    public bool UserFanChanged { get; private set; }

    /// <summary>
    /// Full cooling now, remembering what was set: both fan curves to maximum. Where the curves aren't writable
    /// (unconfirmed models) only Silent is moved to the firmware's Auto mode; Auto and the user's own Advanced curve
    /// are left alone, since forcing Auto could cool less than an aggressive custom curve.
    /// </summary>
    public IReadOnlyList<SetResult> Engage()
    {
        Active = true;
        UserFanChanged = false;
        return Fallback();
    }

    public IReadOnlyList<SetResult> Enforce()
    {
        if (!Active) return Array.Empty<SetResult>();
        var results = new List<SetResult>();
        if (!CurvesWritable)
        {
            if (_movedToAuto && FanValue("auto") is int auto && _ec.TryGet("fanMode") != auto) results.Add(_ec.Set("fanMode", auto));
            return results;
        }
        var fanAdvanced = FanValue("advanced") is int adv && _ec.TryGet("fanMode") == adv;
        var allMax = Sides.All(side => Enumerable.Range(0, FanCurveRules.Points).All(i => _ec.TryGet($"{side}CurveSpeed{i}") == Max));
        if (!fanAdvanced || !allMax) results.AddRange(MaxCurve());
        return results;
    }

    public void UserChangedFans() => UserFanChanged = true;

    public FailsafeRelease Release()
    {
        var results = new List<SetResult>();
        // If any "before" value couldn't be read at engage, don't guess: the caller re-applies the profile.
        var curves = CurvesWritable;
        if (!curves)
        {
            // Profiles don't touch the fans here, so what was set before is still what should run.
            if (_movedToAuto && _fanModeBefore is int before) results.Add(_ec.Set("fanMode", before));
            Active = UserFanChanged = _movedToAuto = false;
            _fanModeBefore = null;
            return new FailsafeRelease(results, false);
        }
        var complete = _fanModeBefore is not null && _cpuBefore is not null && _gpuBefore is not null;
        if (complete && !UserFanChanged)
        {
            if (curves)
            {
                results.AddRange(RestoreCurve("cpu", _cpuBefore));
                results.AddRange(RestoreCurve("gpu", _gpuBefore));
            }
            results.Add(_ec.Set("fanMode", _fanModeBefore!.Value)); // curve first, then the mode that uses it
        }
        var pending = UserFanChanged || !complete;
        Active = UserFanChanged = false;
        _fanModeBefore = null;
        _cpuBefore = _gpuBefore = null;
        return new FailsafeRelease(results, pending);
    }

    private static readonly string[] Sides = { "cpu", "gpu" };

    private int? FanValue(string mode) =>
        _ec.Profile?.Settings.GetValueOrDefault("fanMode")?.Values is { } v && v.TryGetValue(mode, out var value) ? value : null;

    /// <summary>Full speed on this model's fan scale.</summary>
    private int Max => _ec.Profile?.Curve.MaxSpeed ?? FanPercent.Max;

    private bool CurvesWritable => FanCurveRules.Writable(_ec.Profile);

    private IReadOnlyList<SetResult> Fallback()
    {
        if (_fanModeBefore is null)
        {
            _fanModeBefore = _ec.TryGet("fanMode");
            _cpuBefore = ReadCurve("cpu");
            _gpuBefore = ReadCurve("gpu");
        }
        if (CurvesWritable) return MaxCurve();
        if (FanValue("silent") is int silent && _fanModeBefore == silent && FanValue("auto") is int auto)
        {
            _movedToAuto = true;
            return new[] { _ec.Set("fanMode", auto) };
        }
        return Array.Empty<SetResult>();
    }

    /// <summary>Every point to max (continuing past failures), then advanced mode so the EC uses it.</summary>
    private IReadOnlyList<SetResult> MaxCurve()
    {
        var results = new List<SetResult>();
        foreach (var side in Sides)
            for (var i = 0; i < FanCurveRules.Points; i++)
                results.Add(_ec.Set($"{side}CurveSpeed{i}", Max));
        if (FanValue("advanced") is int adv) results.Add(_ec.Set("fanMode", adv));
        return results;
    }

    private int[]? ReadCurve(string side)
    {
        var values = Enumerable.Range(0, FanCurveRules.Points).Select(i => _ec.TryGet($"{side}CurveSpeed{i}")).ToArray();
        return values.All(v => v is not null) ? values.Select(v => v!.Value).ToArray() : null;
    }

    private IEnumerable<SetResult> RestoreCurve(string side, int[]? curve)
    {
        if (curve is null) yield break;
        for (var i = 0; i < curve.Length; i++) yield return _ec.Set($"{side}CurveSpeed{i}", curve[i]);
    }
}
