using System.Text.Json;

namespace WyvernCenter.Core.Safety;

/// <summary>
/// Original raw byte of every writable setting, captured before Wyvern Center first changes anything.
/// Entries are only ever added, never replaced, so the file always holds the pre-Wyvern Center state.
/// </summary>
public sealed class SnapshotStore
{
    private readonly string _path;

    public SnapshotStore(string path) => _path = path;

    public bool Exists => File.Exists(_path);

    public IReadOnlyDictionary<string, int> Load()
    {
        if (!File.Exists(_path)) return new Dictionary<string, int>();
        return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_path)) ?? new Dictionary<string, int>();
    }

    /// <summary>False when the file exists but can't be read (corrupt): callers must not guess or overwrite it.</summary>
    public bool TryLoad(out IReadOnlyDictionary<string, int> values)
    {
        try
        {
            values = Load();
            return true;
        }
        catch (Exception)
        {
            values = new Dictionary<string, int>();
            return false;
        }
    }

    /// <summary>Adds entries for names not yet recorded. Returns how many were added, or -1 if the file is corrupt.</summary>
    public int AddMissing(IEnumerable<(string Name, Func<int> Read)> candidates) => AddMissing(candidates, out _);

    /// <param name="recorded">The candidates that are in the snapshot afterwards (already there or just added).</param>
    public int AddMissing(IEnumerable<(string Name, Func<int> Read)> candidates, out IReadOnlyCollection<string> recorded)
    {
        var present = new List<string>();
        recorded = present;
        if (!TryLoad(out var loaded)) return -1;
        var current = new Dictionary<string, int>(loaded);
        var added = 0;
        foreach (var (name, read) in candidates)
        {
            if (current.ContainsKey(name)) { present.Add(name); continue; }
            try
            {
                current[name] = read();
                added++;
                present.Add(name);
            }
            catch
            {
                // unreadable right now: try again before the next write
            }
        }
        if (added > 0)
        {
            var temp = _path + ".tmp"; // the folder is the app's to create: a missing one fails the write (fail closed)
            File.WriteAllText(temp, JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true); // atomic replace: a crash never leaves a half-written snapshot
        }
        return added;
    }
}
