using WyvernCenter.Core.Safety;

namespace WyvernCenter.Core.Verify;

/// <summary>
/// The hardware side of "Verify my laptop": watches settings while the user presses keys, and makes each test change
/// through <see cref="EcController.TestSet"/>, remembering the value from before so it always goes back (also when
/// the wizard is closed half-way). Call from one thread (the hardware queue).
/// </summary>
public sealed class VerifySession
{
    private readonly EcController _ec;
    private readonly Func<bool, IReadOnlyList<SetResult>>? _maxCooling;
    private readonly Dictionary<string, int> _before = new();
    private readonly Dictionary<string, List<int>> _seen = new();
    private bool _maxCoolingOn;

    /// <param name="maxCooling">Turns the app's Max Cooling on/off (the Max Cooling step); null where it isn't available.</param>
    public VerifySession(EcController ec, Func<bool, IReadOnlyList<SetResult>>? maxCooling = null)
    {
        _ec = ec;
        _maxCooling = maxCooling;
    }

    public bool Begin() => _ec.BeginTest();

    /// <summary>Puts back anything still changed, then ends the test session.</summary>
    public IReadOnlyList<string> End()
    {
        var notes = new List<string>();
        foreach (var name in _before.Keys.ToList()) notes.Add(RevertSetting(name).Note);
        if (_maxCoolingOn) notes.Add(SetMaxCooling(false).Note);
        _ec.EndTest();
        return notes;
    }

    // ---- Observe ----------------------------------------------------------------------------

    /// <summary>Reads the setting once and remembers each new value. Returns the current value.</summary>
    public int? Track(string setting)
    {
        var v = _ec.TryGet(setting);
        if (v is int value)
        {
            var seen = _seen.TryGetValue(setting, out var list) ? list : _seen[setting] = new List<int>();
            if (seen.Count == 0 || seen[^1] != value) seen.Add(value);
        }
        return v;
    }

    /// <summary>How many times the value changed while watching.</summary>
    public int Changes(string setting) => _seen.TryGetValue(setting, out var l) ? Math.Max(0, l.Count - 1) : 0;

    public string ObservedNote(string setting) =>
        _seen.TryGetValue(setting, out var l) && l.Count > 0
            ? $"seen {string.Join(" → ", l.Select(v => Show(setting, v)))} ({Changes(setting)} change{(Changes(setting) == 1 ? "" : "s")})"
            : "no reading";

    // ---- Change -----------------------------------------------------------------------------

    public (bool Ok, string Note) Apply(VerifyStep step)
    {
        if (step.Setting == VerifyPlan.MaxCooling) return SetMaxCooling(true);
        var name = step.Setting!;
        if (!_ec.CanTest(name)) return (false, $"{name}: can't be tested on this laptop");
        if (_ec.Profile?.Settings.GetValueOrDefault(name) is not { } def) return (false, $"{name}: unknown");
        int current;
        try { current = _ec.Get(name); }
        catch (Exception ex) { return (false, $"{name}: couldn't read it first ({ex.Message})"); }
        if (VerifyPlan.TestValue(SettingDefView.Of(def), current) is not int test) return (false, $"{name}: no other value to try");

        _before.TryAdd(name, current);
        var r = _ec.TestSet(name, test);
        var back = _ec.TryGet(name);
        return (r.Ok && back == test,
            $"{Show(name, current)} → {Show(name, test)}: {(r.Ok ? "accepted" : r.Message)}, read back {(back is int b ? Show(name, b) : "nothing")}");
    }

    public (bool Ok, string Note) Revert(VerifyStep step) =>
        step.Setting == VerifyPlan.MaxCooling ? SetMaxCooling(false) : RevertSetting(step.Setting!);

    private (bool Ok, string Note) RevertSetting(string name)
    {
        if (!_before.TryGetValue(name, out var original)) return (true, "");
        var r = _ec.TestSet(name, original, restoring: true);
        var back = _ec.TryGet(name);
        var ok = r.Ok && back == original;
        if (ok) _before.Remove(name);
        return (ok, $"put back {Show(name, original)}: {(ok ? "done" : $"read back {(back is int b ? Show(name, b) : "nothing")} ({r.Message})")}");
    }

    private (bool Ok, string Note) SetMaxCooling(bool on)
    {
        if (_maxCooling is null) return (false, "Max Cooling isn't available here");
        var results = _maxCooling(on);
        _maxCoolingOn = on;
        var failed = results.Where(r => !r.Ok).ToList();
        return (failed.Count == 0,
            $"Max Cooling {(on ? "on" : "off")}: {(failed.Count == 0 ? "done" : string.Join("; ", failed.Select(f => $"{f.Setting} {f.Message}")))}");
    }

    /// <summary>Modes read better in hex (0xC1), levels and percentages in decimal.</summary>
    public static string Show(string setting, int value) => setting is "shiftMode" or "fanMode" ? $"0x{value:X2}" : value.ToString();
}
