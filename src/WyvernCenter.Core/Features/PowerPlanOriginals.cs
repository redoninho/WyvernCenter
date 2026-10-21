using System.Text.Json;

namespace WyvernCenter.Core.Features;

/// <summary>One power plan's CPU settings before Wyvern Center changed them (boost mode, max state %, max frequency).</summary>
public sealed record PlanValues(uint AcBoost, uint DcBoost, uint AcMax, uint DcMax, uint AcFreq, uint DcFreq);

/// <summary>
/// The original CPU power settings of every Windows power plan Wyvern Center has written to, recorded before its first
/// write to that plan, so Restore can put each plan back (switching plans must not leave a hidden cap behind).
/// Reads the old single-plan file format too.
/// </summary>
public sealed class PowerPlanOriginals
{
    private readonly string _path;

    private PowerPlanOriginals(string path) => _path = path;

    public Dictionary<Guid, PlanValues> Plans { get; } = new();

    private sealed record Legacy(Guid Scheme, uint AcBoost, uint DcBoost, uint AcMax, uint DcMax, uint AcFreq = 0, uint DcFreq = 0);

    public static PowerPlanOriginals Load(string path)
    {
        var o = new PowerPlanOriginals(path);
        if (!File.Exists(path)) return o;
        try
        {
            Parse(o, File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // unreadable: keep it for inspection and start fresh rather than blocking every CPU setting
            File.Copy(path, path + ".bad", overwrite: true);
            File.Delete(path);
            o.Plans.Clear();
        }
        return o;
    }

    private static void Parse(PowerPlanOriginals o, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("Scheme", out _))
        {
            var l = JsonSerializer.Deserialize<Legacy>(json)!;
            o.Plans[l.Scheme] = new PlanValues(l.AcBoost, l.DcBoost, l.AcMax, l.DcMax, l.AcFreq, l.DcFreq);
        }
        else
        {
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<Guid, PlanValues>>(json) ?? new())
                o.Plans[k] = v;
        }
    }

    /// <returns>True when the plan wasn't recorded yet (and now is).</returns>
    public bool AddIfMissing(Guid plan, PlanValues values) => Plans.TryAdd(plan, values);

    public void Remove(Guid plan) => Plans.Remove(plan);

    public void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Plans));
        File.Move(temp, _path, overwrite: true);
    }

    public void Clear()
    {
        Plans.Clear();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
