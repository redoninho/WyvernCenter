namespace WyvernCenter;

/// <summary>
/// Wyvern Center runs elevated, so everything it reads back or executes must be admin-only. Both the installed copy and
/// its data live under %ProgramFiles%\Wyvern Center: only administrators can create or change anything there, so no
/// non-admin process can pre-create, swap (junction/symlink) or edit these files. If the data folder can't be
/// prepared, Wyvern Center fails closed: read-only, nothing persisted.
/// </summary>
public static class SecureStorage
{
    public static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wyvern Center");

    /// <summary>Where earlier versions lived, newest name first (MSI-Helper, then M-Helper); data is moved from them on first start.</summary>
    public static readonly IReadOnlyList<string> LegacyInstallDirs = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MSI-Helper"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "M-Helper"),
    };

    public static readonly string DataDir = Path.Combine(InstallDir, "data");

    /// <summary>True once <see cref="PrepareDataDir"/> succeeded; nothing may be read from or written to DataDir otherwise.</summary>
    public static bool Ready { get; private set; }

    /// <summary>Creates the data folder. Returns a warning, or null when all is well.</summary>
    private static bool _prepared;
    private static string? _prepareWarning;

    /// <remarks>Runs once per process: Program (instance lock) and AppController must see the same answer, or a
    /// folder freed in between could leave a writable instance that doesn't hold the lock.</remarks>
    public static string? PrepareDataDir()
    {
        if (_prepared) return _prepareWarning;
        _prepared = true;
        return _prepareWarning = PrepareOnce();
    }

    private static string? PrepareOnce()
    {
        try
        {
            // One rename keeps the snapshot of original values (what Restore puts back) in one place, never two.
            foreach (var legacy in LegacyInstallDirs)
                if (WyvernCenter.Core.Safety.DataFolderMigration.Run(Path.Combine(legacy, "data"), DataDir) == WyvernCenter.Core.Safety.DataFolderMigration.Result.Blocked)
                    return $"The data folder in {legacy} couldn't be moved (is an older copy still running?). Close it and restart Wyvern Center.";
            var di = Directory.CreateDirectory(DataDir);
            if (di.Attributes.HasFlag(FileAttributes.ReparsePoint) || new DirectoryInfo(InstallDir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return "Wyvern Center's data folder is a link; refusing to use it.";
            var probe = Path.Combine(DataDir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            Ready = true;
            return null;
        }
        catch (Exception ex)
        {
            return "Couldn't prepare the data folder (read-only mode): " + ex.Message;
        }
    }

    public static bool RunningInstalled =>
        string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the published single-file build (no loose DLLs that could be swapped or planted).</summary>
    public static readonly string[] NativeCompanions =
    {
        "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "PresentationNative_cor3.dll", "vcruntime140_cor3.dll", "wpfgfx_cor3.dll",
    };

    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(SecureStorage).Assembly.Location);

    /// <summary>
    /// Copies the running single-file exe (and nothing else) into Program Files and returns its path. Dev builds are
    /// refused: their folder is user-writable and full of loose DLLs, which an elevated logon task must never load.
    /// </summary>
    public static string Install()
    {
        var name = Path.GetFileName(Environment.ProcessPath!);
        var target = Path.Combine(InstallDir, name);
        if (RunningInstalled) return target;
        if (!IsSingleFile)
            throw new InvalidOperationException("This is a development build. Run publish.ps1 and start dist\\WyvernCenter.exe to install for startup.");
        Directory.CreateDirectory(InstallDir);
        File.Copy(Environment.ProcessPath!, target, overwrite: true);
        // The runtime's few native companions that can't live inside the bundle; an explicit list, never a wildcard.
        var source = Path.GetDirectoryName(Environment.ProcessPath!)!;
        foreach (var dll in NativeCompanions)
        {
            var from = Path.Combine(source, dll);
            if (File.Exists(from)) File.Copy(from, Path.Combine(InstallDir, dll), overwrite: true);
        }
        return target;
    }
}
