using System.Text;
using WyvernCenter.Core.Safety;

namespace WyvernCenter.Core.Verify;

/// <summary>
/// Readings: live values to judge. Observe: press a key, Wyvern watches. Change: Wyvern changes it, then puts it back.
/// Measure: Wyvern changes it under CPU load and compares power and clock itself (see <see cref="ModeMeasurement"/>).
/// </summary>
public enum StepKind { Readings, Observe, Change, Measure }

public enum StepAnswer { Unanswered, Yes, No, Skipped }

/// <param name="Setting">The setting watched or changed; null for the readings step and Max Cooling.</param>
/// <param name="Optional">Not every MSI laptop has this key or feature: Skip is a normal answer.</param>
public sealed record VerifyStep(string Id, StepKind Kind, string Title, string Instruction, string Question, string? Setting, bool Optional = false);

/// <param name="Notes">What Wyvern saw itself (values before / during / after, read-backs).</param>
public sealed record VerifyResult(string Id, string Title, StepAnswer Answer, string Notes);

/// <summary>
/// The steps of "Verify my laptop" for this laptop: only what its profile can read (or the wizard may change). The
/// readings come first and change nothing; the change steps each change one setting briefly and put it back.
/// </summary>
public static class VerifyPlan
{
    public const string MaxCooling = "maxCooling";
    /// <summary>The fan-curve check step (WMI v2): fan mode plus both curves, measured by fan speed.</summary>
    public const string FanCurve = "fanCurve";

    public const int CurvePointCount = 6;
    public static readonly IReadOnlyList<string> CurvePoints =
        new[] { "cpu", "gpu" }.SelectMany(side => Enumerable.Range(0, CurvePointCount).Select(i => $"{side}CurveSpeed{i}")).ToList();
    /// <summary>Where testers send their report (shown at the end of the wizard).</summary>
    public const string ReportEmail = "info@noptic.net";

    /// <summary>The MSI level names the app uses (Edit profile), for the profile's shiftMode value names.</summary>
    public static string LevelName(string? valueName) => valueName switch
    {
        "eco" => "Super Battery",
        "comfort" => "Normal",
        "turbo" => "Extreme",
        null => "this level",
        _ => char.ToUpperInvariant(valueName[0]) + valueName[1..],
    };

    private const string KbdSkip = "Skip if your keyboard has no backlight, or has RGB lighting (that's run by its own chip, not the firmware).";

    public static IReadOnlyList<VerifyStep> For(EcController ec, bool maxCoolingAvailable)
    {
        var steps = new List<VerifyStep>
        {
            new("sensors", StepKind.Readings, "Temperatures and fans",
                "These are the temperatures and fan speeds Wyvern Center reads from the laptop. The GPU shows – while it's " +
                "asleep, which is normal on laptops that switch between graphics chips.",
                "Do they look right? Temperatures roughly 30–100 °C, and the fans speed up when the laptop is busy.", null),
        };
        bool Readable(string s) => ec.IsReadable(s);
        bool Testable(string s) => ec.Profile?.Settings.GetValueOrDefault(s)?.Testable == true && ec.IsReadable(s);

        if (Readable("kbdBacklight"))
            steps.Add(new("kbdKey", StepKind.Observe, "Keyboard light key",
                "Press your keyboard backlight key a few times (often Fn + F8, Fn + F9 or Fn + F10). " + KbdSkip,
                "Did the keyboard light change, and did Wyvern's number change with it?", "kbdBacklight", Optional: true));
        if (Readable("coolerBoost"))
            steps.Add(new("boostKey", StepKind.Observe, "Cooler Boost button",
                "If your laptop has a Cooler Boost button or key, press it, wait for the fans, then press it again. Skip if it hasn't.",
                "Did Wyvern show Cooler Boost turning on and off with the fans?", "coolerBoost", Optional: true));

        if (Testable("kbdBacklight"))
            steps.Add(new("kbdChange", StepKind.Change, "Keyboard light (test)",
                "Wyvern will change the keyboard light level, then put it back. " + KbdSkip,
                "Did the keyboard light change when Wyvern changed it?", "kbdBacklight", Optional: true));
        if (Testable("shiftMode"))
            steps.Add(new("modeMeasure", StepKind.Measure, "MSI level (measured)",
                "The MSI level (Super Battery, Normal, Extreme; MSI Center calls them its modes) sets the firmware's power " +
                $"limits, so it only shows under load. Wyvern runs the CPU at full load for {ModeMeasurement.PhaseSeconds} s in your " +
                $"current level, then {ModeMeasurement.PhaseSeconds} s in another, compares power and clock, and puts your level back. " +
                "The fans may get louder.",
                "Nothing to judge: Wyvern measures it. Click Run it.", "shiftMode"));
        if (Testable("fanMode") && CurvePoints.All(Testable))
            steps.Add(new("fanCurveCheck", StepKind.Measure, "Custom fan curves (measured)",
                "Wyvern raises both fan curves to their own top speed and tries the setting that switches custom curves on " +
                "(MSI Center's way first, then the other), watching whether the fans speed up. Then it puts your fans back. " +
                "Best when the laptop is cool and quiet. Up to a minute; the fans get loud.",
                "Nothing to judge: Wyvern measures it. Click Run it.", FanCurve));
        if (Testable("coolerBoost"))
            steps.Add(new("boostChange", StepKind.Change, "Cooler Boost (test)",
                "Wyvern will turn Cooler Boost on for a few seconds, then off again. The fans get loud.",
                "Did the fans go to full speed?", "coolerBoost"));
        else if (maxCoolingAvailable)
            steps.Add(new("maxCoolingChange", StepKind.Change, "Max Cooling (test)",
                "Wyvern will set both fans to full speed for a few seconds, then put your fans back. The fans get loud.",
                "Did the fans go to full speed?", MaxCooling));
        if (Testable("chargeLimit"))
            steps.Add(new("chargeChange", StepKind.Change, "Battery charge limit (test)",
                "Wyvern will set the charge limit to a different value, then put it back.",
                "If the battery is above the new limit, does Windows' battery icon say \"not charging\"? Skip if you can't tell.",
                "chargeLimit", Optional: true));
        return steps;
    }

    /// <summary>
    /// The value a change step writes: clearly different from now, inside the setting's allowed values. Null when
    /// nothing else is allowed.
    /// </summary>
    public static int? TestValue(SettingDefView s, int current) => s switch
    {
        // Named levels: eco (lower limits: the safe direction, and clearly measurable under load), else turbo, else any
        // other allowed value.
        { Values: { Count: > 0 } v } => new[] { "eco", "turbo" }.Where(v.ContainsKey).Select(k => v[k])
            .Concat(v.Values).Where(x => x != current).Select(x => (int?)x).FirstOrDefault(),
        { Min: int min, Max: int max } when s.Name == "chargeLimit" => current > 80 ? 80 : 100,
        { Min: int min, Max: int max } => current == max ? min : max,
        _ => null,
    };

    /// <summary>The "Verify my laptop" section of a support report.</summary>
    public static string ToMarkdown(IReadOnlyList<VerifyResult> results, string firmware, DateTime when)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Verify my laptop").AppendLine();
        sb.AppendLine($"Run {when:yyyy-MM-dd HH:mm} on firmware {(firmware == "" ? "unknown" : firmware)}.").AppendLine();
        sb.AppendLine("| Step | Answer | What Wyvern saw |").AppendLine("|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {r.Title} | {r.Answer} | {r.Notes.Replace("|", "/").Replace("\n", " ")} |");
        return sb.ToString();
    }
}

/// <summary>The parts of a setting the test-value choice needs.</summary>
public sealed record SettingDefView(string Name, IReadOnlyDictionary<string, int>? Values, int? Min, int? Max)
{
    public static SettingDefView Of(Profile.SettingDef s) => new(s.Name, s.Values, s.Min, s.Max);
}
