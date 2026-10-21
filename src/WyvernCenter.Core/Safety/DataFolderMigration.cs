namespace WyvernCenter.Core.Safety;

/// <summary>
/// Moves the data folder from its old name (Program Files\M-Helper\data) to the new one in a single rename, so the
/// snapshot of original values (what Restore puts back) is never lost or split. Never merges or overwrites. Afterwards
/// a file takes the old folder's place, so an old copy started by mistake can't recreate it and runs read-only.
/// </summary>
public static class DataFolderMigration
{
    public enum Result { NothingToDo, Moved, Blocked }

    /// <summary>The files that hold originals or the user's setup; logs and reports don't count.</summary>
    public static readonly IReadOnlyList<string> KeyFiles = new[] { "snapshot.json", "settings.json", "cpu-originals.json", "services.json" };

    public static Result Run(string legacyData, string newData)
    {
        if (!Directory.Exists(legacyData)) return Result.NothingToDo; // never existed, or already moved (now a file)
        var legacyRoot = Path.GetDirectoryName(legacyData)!;
        if (IsLink(legacyData) || IsLink(legacyRoot)) return Result.Blocked; // never follow a link planted in its place
        if (!HasKeyFiles(legacyData)) return Result.NothingToDo;
        string? aside = null;
        if (Directory.Exists(newData))
        {
            // Both hold originals: which are the real ones can't be known, so nothing moves and the app stays read-only.
            if (IsLink(newData) || HasKeyFiles(newData)) return Result.Blocked;
            aside = newData + ".stray-" + DateTime.Now.ToString("yyyyMMddHHmmss"); // only logs: kept, out of the way
        }
        var newRoot = Path.GetDirectoryName(newData)!;
        var createdRoot = !Directory.Exists(newRoot);
        try
        {
            Directory.CreateDirectory(newRoot);
            if (aside is not null) Directory.Move(newData, aside);
            try
            {
                Directory.Move(legacyData, newData);
            }
            catch
            {
                if (aside is not null) Directory.Move(aside, newData);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (an older copy still running) or not permitted: leave everything where it was.
            if (createdRoot) try { Directory.Delete(newRoot); } catch (IOException) { }
            return Result.Blocked;
        }
        try { File.WriteAllText(legacyData, "Wyvern Center moved this folder to " + newData + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Result.Moved;
    }

    private static bool HasKeyFiles(string dir) => KeyFiles.Any(f => File.Exists(Path.Combine(dir, f)));

    private static bool IsLink(string path) => new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
}
