using WyvernCenter.Core.Safety;

namespace WyvernCenter.Core.Features;

/// <summary>Fan mode and curve writes. Curves are validated first and written before switching to advanced mode.</summary>
public static class FanControl
{
    public static int[] ReadCurve(EcController ec, string side) =>
        Enumerable.Range(0, FanCurveRules.Points).Select(i => ec.Get($"{side}CurveSpeed{i}")).ToArray();

    public static int[] ReadThresholds(EcController ec, string side) =>
        Enumerable.Range(0, FanCurveRules.Points).Select(i => ec.Get($"{side}CurveTemp{i}")).ToArray();

    /// <summary>Validates both curves, writes them, then switches to advanced mode (the firmware follows the curve registers).</summary>
    public static IReadOnlyList<SetResult> ApplyCurves(EcController ec, IReadOnlyList<int> cpuCurve, IReadOnlyList<int> gpuCurve)
    {
        var profile = ec.Profile;
        if (!FanCurveRules.Writable(profile)) return Array.Empty<SetResult>(); // unconfirmed model: fans are left alone
        var modes = profile?.Settings.GetValueOrDefault("fanMode")?.Values;
        if (profile is null || modes is null || !modes.TryGetValue("advanced", out var advanced))
            return new[] { new SetResult("fanMode", SetOutcome.Rejected, "no advanced fan mode for this model") };

        var curves = new[] { ("cpu", cpuCurve), ("gpu", gpuCurve) };
        foreach (var (side, curve) in curves)
        {
            var errors = FanCurveRules.Validate(curve, profile.Curve);
            if (errors.Count > 0) return new[] { new SetResult("fanCurve", SetOutcome.Rejected, $"{side}: {string.Join(" ", errors)}") };
        }
        // Advanced mode is normally already live (every profile uses it), so the firmware follows each register the
        // moment it is written. Write in two passes so the curve is valid after every single write: first raise
        // points to max(old, new) from the hottest down, then lower them to the new values from the coolest up.
        // A write that fails part-way leaves a curve that still rises and still reaches full cooling.
        var results = new List<SetResult>();
        foreach (var (side, curve) in curves)
        {
            var old = Enumerable.Range(0, FanCurveRules.Points).Select(i => ec.TryGet($"{side}CurveSpeed{i}") ?? profile.Curve.MaxSpeed).ToArray();
            var current = (int[])old.Clone();
            for (var i = FanCurveRules.Points - 1; i >= 0; i--)
                if (!Write(Math.Max(old[i], curve[i]), i)) return results;
            for (var i = 0; i < FanCurveRules.Points; i++)
                if (!Write(curve[i], i)) return results;

            bool Write(int value, int i)
            {
                if (current[i] == value) return true;
                var r = ec.Set($"{side}CurveSpeed{i}", value);
                results.Add(r);
                if (r.Ok) current[i] = value;
                return r.Ok;
            }
        }
        results.Add(ec.Set("fanMode", advanced));
        return results;
    }
}
