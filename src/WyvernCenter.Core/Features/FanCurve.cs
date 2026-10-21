using WyvernCenter.Core.Profile;

namespace WyvernCenter.Core.Features;

/// <summary>Six fan-speed points (0–150) at the EC's own temperature thresholds, as MSI's advanced mode uses.</summary>
public static class FanCurveRules
{
    public const int Points = 6;

    /// <summary>The model profile lets every curve point be written (not so on unconfirmed models).</summary>
    public static bool Writable(ModelProfile? p) => p is not null && new[] { "cpu", "gpu" }.All(side => Enumerable.Range(0, Points)
        .All(i => p.Settings.TryGetValue($"{side}CurveSpeed{i}", out var s) && !s.ReadOnly && s.Verified));

    public static IReadOnlyList<string> Validate(IReadOnlyList<int> speeds, CurveRules rules)
    {
        var errors = new List<string>();
        if (speeds.Count != Points)
        {
            errors.Add($"A curve has {Points} points, not {speeds.Count}.");
            return errors;
        }
        for (var i = 0; i < Points; i++)
        {
            if (speeds[i] < rules.MinSpeed || speeds[i] > rules.MaxSpeed)
                errors.Add($"Point {i + 1} must be between {rules.MinSpeed} and {rules.MaxSpeed}.");
            if (i > 0 && speeds[i] < speeds[i - 1])
                errors.Add($"Point {i + 1} must not be lower than point {i}: fans must rise with temperature.");
        }
        if (speeds[rules.FloorAtIndex] < rules.FloorSpeed)
            errors.Add($"Point {rules.FloorAtIndex + 1} (hottest) must be at least {rules.FloorSpeed}.");
        return errors;
    }

    public static int[] Max(CurveRules rules) => Enumerable.Repeat(rules.MaxSpeed, Points).ToArray();
}
