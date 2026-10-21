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
        // Fan mode first: once it's back, a raised test curve is no longer followed.
        foreach (var name in _before.Keys.OrderBy(n => n == "fanMode" ? 0 : 1).ToList()) notes.Add(RevertSetting(name).Note);
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
        return Write(name, current, test);
    }

    // ---- Fan-curve check (WMI v2) -----------------------------------------------------------

    /// <summary>
    /// Both curves to their own top speed: every point becomes the curve's fastest point, so no scale has to be
    /// assumed and the fans can only get faster. Hottest point first, so the curve keeps rising after every write.
    /// </summary>
    public (bool Ok, string Note) RaiseCurves()
    {
        var notes = new List<string>();
        foreach (var side in new[] { "cpu", "gpu" })
        {
            int[] points;
            try { points = Enumerable.Range(0, VerifyPlan.CurvePointCount).Select(i => _ec.Get($"{side}CurveSpeed{i}")).ToArray(); }
            catch (Exception ex) { return (false, $"{side} curve: couldn't read it ({ex.Message})"); }
            var top = points.Max();
            for (var i = points.Length - 1; i >= 0; i--)
            {
                if (points[i] == top) continue;
                var r = TestWrite($"{side}CurveSpeed{i}", top);
                if (!r.Ok) return (false, r.Note);
            }
            notes.Add($"{side} curve {string.Join(",", points)} → all {top}");
        }
        return (true, string.Join("; ", notes));
    }

    public (bool Ok, string Note) SetFanMode(int value) => TestWrite("fanMode", value);

    public (bool Ok, string Note) PutFanModeBack() => RevertSetting("fanMode");

    /// <summary>Fan mode first (then the raised curve is no longer followed), then every curve point.</summary>
    public (bool Ok, string Note) PutFansBack()
    {
        var results = new[] { RevertSetting("fanMode") }.Concat(VerifyPlan.CurvePoints.Select(RevertSetting)).ToList();
        var failed = results.Where(r => !r.Ok).ToList();
        return (failed.Count == 0, failed.Count == 0 ? "fans put back" : string.Join("; ", failed.Select(f => f.Note)));
    }

    private (bool Ok, string Note) TestWrite(string name, int value)
    {
        if (!_ec.CanTest(name)) return (false, $"{name}: can't be tested on this laptop");
        int current;
        try { current = _ec.Get(name); }
        catch (Exception ex) { return (false, $"{name}: couldn't read it first ({ex.Message})"); }
        var (ok, note) = Write(name, current, value);
        return (ok, $"{name} {note}");
    }

    /// <summary>One test write: the value from before is kept (first time only), so it always goes back.</summary>
    private (bool Ok, string Note) Write(string name, int current, int value)
    {
        _before.TryAdd(name, current);
        var r = _ec.TestSet(name, value);
        var back = _ec.TryGet(name);
        return (r.Ok && back == value,
            $"{Show(name, current)} → {Show(name, value)}: {(r.Ok ? "accepted" : r.Message)}, read back {(back is int b ? Show(name, b) : "nothing")}");
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
