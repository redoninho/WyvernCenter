using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WyvernCenter.Core.Features;
using WyvernCenter.Core.Safety;

namespace WyvernCenter;

public sealed class AppSettings
{
    public static readonly string Dir = SecureStorage.DataDir;
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>The profile in use; null until the user picks one (nothing is written before that).</summary>
    public string? ActiveProfile { get; set; }
    public List<Profile>? Profiles { get; set; }
    public bool AutoSwitchEnabled { get; set; }
    public string? ProfileOnAc { get; set; }
    public string? ProfileOnBattery { get; set; }
    public bool RefreshAuto { get; set; }
    /// <summary>The EC firmware the user allowed experimental changes on (unconfirmed model); bound to that firmware.</summary>
    public string? ExperimentalFirmware { get; set; }
    /// <summary>Battery charge limit through MSI's Dragon Center service (WMI v1 laptops): started only for the change.</summary>
    public bool UseMsiService { get; set; }
    /// <summary>The charge limit the user last set (put back at start-up if the laptop lost it, e.g. after a full power-off).</summary>
    public int? ChargeLimit { get; set; }
    /// <summary>The NVIDIA GPU's limits, read once (reading them wakes the GPU). Null until found.</summary>
    public GpuLimits? GpuLimits { get; set; }
    /// <summary>Windows' power mode before Wyvern Center first changed it (Restore puts it back).</summary>
    public PowerOverlay? OriginalPowerMode { get; set; }
    /// <summary>UI scale in percent (70–125), like D-Helper.</summary>
    public int UiScale { get; set; } = 100;
    /// <summary>Profile hotkey: RegisterHotKey modifiers and virtual key (default Ctrl + Alt + P).</summary>
    public uint HotkeyModifiers { get; set; } = HotkeyWindow.ModControl | HotkeyWindow.ModAlt;
    public int HotkeyKey { get; set; } = 0x50;

    /// <summary>In-game overlay: shown, background opacity (0–90 %), corner (0 TL, 1 TR, 2 BL, 3 BR), lines, hotkey (Ctrl + Alt + O).</summary>
    public bool OverlayOn { get; set; }
    public int OverlayOpacity { get; set; } = 45;
    public int OverlayCorner { get; set; }
    public bool OverlayFps { get; set; } = true;
    public bool OverlayCpu { get; set; } = true;
    public bool OverlayGpu { get; set; } = true;
    public bool OverlaySys { get; set; } = true;
    public bool OverlayPower { get; set; } = true;
    public bool OverlayGraph { get; set; } = true;
    public uint OverlayHotkeyModifiers { get; set; } = HotkeyWindow.ModControl | HotkeyWindow.ModAlt;
    public int OverlayHotkeyKey { get; set; } = 0x4F;

    // v0.2.0 fields, read once for migration and then dropped from the file.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ModeOnAc { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ModeOnBattery { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                if (s is not null) return s;
            }
        }
        catch
        {
            try { File.Copy(FilePath, Path.Combine(Dir, "settings.bad.json"), overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    /// <summary>The last save error (null when the last save worked).</summary>
    public static string? SaveError { get; private set; }

    /// <summary>Writes a temp file and swaps it in, so a crash mid-write never leaves a broken settings file. Never throws.</summary>
    public void Save()
    {
        if (!SecureStorage.Ready) return; // fail closed: never write outside the admin-only folder
        try
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
            SaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = ex.Message;
        }
    }
}

/// <summary>Run at logon, elevated, without a UAC prompt: a scheduled task with highest privileges.</summary>
public static class StartupTask
{
    private const string TaskName = "Wyvern Center";
    /// <summary>The task's earlier names (MSI-Helper, M-Helper).</summary>
    private static readonly string[] LegacyTaskNames = { "MSI-Helper", "M-Helper" };

    public static bool IsEnabled() => Schtasks("/Query", "/TN", TaskName) == 0 || LegacyTaskNames.Any(n => Schtasks("/Query", "/TN", n) == 0);

    /// <summary>
    /// Replaces a startup task from before the rename (pointing at Program Files\M-Helper) with the new one, then
    /// removes the old installed copy. Returns a message for the log, or null when there was nothing to do.
    /// </summary>
    public static string? MigrateLegacy()
    {
        var legacy = LegacyTaskNames.Where(n => Schtasks("/Query", "/TN", n) == 0).ToList();
        if (legacy.Count == 0) return null;
        if (Set(true) is { } error)
        {
            // Never leave an old copy starting at logon next to this one: switch its task off, and say so.
            foreach (var n in legacy) Schtasks("/Change", "/TN", n, "/DISABLE");
            return "The old startup task was switched off (" + error + "). Turn on Settings → Startup again from an installed build.";
        }
        foreach (var n in legacy) Schtasks("/Delete", "/TN", n, "/F");
        try
        {
            // Only the program files earlier versions installed; the "data" marker file stays, so an old copy runs read-only.
            var running = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
            foreach (var dir in SecureStorage.LegacyInstallDirs.Where(d => !string.Equals(running, d, StringComparison.OrdinalIgnoreCase)))
                foreach (var name in SecureStorage.NativeCompanions.Append("MSI-Helper.exe").Append("M-Helper.exe"))
                {
                    var f = Path.Combine(dir, name);
                    if (File.Exists(f)) File.Delete(f);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "startup task migrated; old copy not removed yet: " + ex.Message;
        }
        return "startup task migrated to " + TaskName;
    }

    /// <summary>
    /// Enabling installs this build to Program Files first: an elevated logon task must never point at a file a
    /// normal user can replace (e.g. a build on the Desktop).
    /// </summary>
    /// <returns>null on success, otherwise a message for the user.</returns>
    public static string? Set(bool enabled)
    {
        if (!enabled)
        {
            foreach (var n in LegacyTaskNames) Schtasks("/Delete", "/TN", n, "/F");
            return Schtasks("/Delete", "/TN", TaskName, "/F") == 0 || !IsEnabled() ? null : "Could not remove the startup task.";
        }
        string exe;
        try { exe = SecureStorage.Install(); }
        catch (Exception ex) { return ex.Message; }
        return Create(exe) ? null : "Could not create the startup task.";
    }

    /// <summary>
    /// Rebuilds a task made by an older version (schtasks defaults: no start on battery, stopped when unplugged or
    /// after 72 hours). Returns a message for the log, or null when there was nothing to do.
    /// </summary>
    public static string? RepairIfOld()
    {
        var (code, xml) = SchtasksOutput("/Query", "/TN", TaskName, "/XML");
        if (code != 0 || !StartupTaskXml.NeedsRepair(xml)) return null;
        // Only ever point the elevated task at the installed copy in Program Files.
        var exe = Path.Combine(SecureStorage.InstallDir, "WyvernCenter.exe");
        if (!xml.Contains(exe, StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) return null;
        return Create(exe) ? "startup task rebuilt (starts on battery, no time limit)" : "startup task could not be rebuilt";
    }

    /// <summary>The XML goes in Program Files (admin-only), so nothing can swap it before schtasks reads it.</summary>
    private static bool Create(string exe)
    {
        var me = WindowsIdentity.GetCurrent();
        var file = Path.Combine(SecureStorage.InstallDir, "startup-task.xml");
        try
        {
            File.WriteAllText(file, StartupTaskXml.Build(exe, me.Name, me.User!.Value), Encoding.Unicode);
            return Schtasks("/Create", "/TN", TaskName, "/XML", file, "/F") == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    private static int Schtasks(params string[] args) => SchtasksOutput(args).ExitCode;

    private static (int ExitCode, string Output) SchtasksOutput(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        p.WaitForExit(10000);
        return (p.ExitCode, output.Wait(1000) ? output.Result : "");
    }
}
