using System.ServiceProcess;
using WyvernCenter.Core.Profile;

namespace WyvernCenter.Core.Features;

public sealed record ServiceState(string Name, ServiceStartMode StartMode, bool Running);

public interface IServiceControl
{
    /// <summary>Null when the service isn't installed.</summary>
    ServiceState? Query(string name);
}

/// <summary>
/// Whether MSI's own apps (Dragon Center, MSI Center) could interfere: their services rewrite the fan curves and
/// performance mode behind Wyvern Center's back. Read-only: Wyvern Center tells the user what to disable and never
/// changes services itself.
/// </summary>
public static class MsiServices
{
    /// <summary>Every MSI service named in any model profile.</summary>
    public static IReadOnlyList<string> Known =>
        ProfileCatalog.Models.Append(ProfileCatalog.Generic).Append(ProfileCatalog.GenericFor(FirmwareInterface.Wmi2))
            .SelectMany(p => p.MsiServices).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The installed ones, in the given order.</summary>
    public static IReadOnlyList<ServiceState> Installed(IServiceControl sc, IEnumerable<string> names) =>
        names.Select(sc.Query).OfType<ServiceState>().ToList();

    /// <summary>Running now, or set to start with Windows.</summary>
    public static bool Interferes(ServiceState s) => s.Running || s.StartMode is ServiceStartMode.Automatic or ServiceStartMode.Boot or ServiceStartMode.System;

    public static string Describe(ServiceState s) =>
        s.Running ? "running" : s.StartMode switch
        {
            ServiceStartMode.Disabled => "disabled",
            ServiceStartMode.Manual => "stopped (starts when an MSI app asks)",
            _ => "stopped (starts with Windows)",
        };

    /// <summary>Short footer text.</summary>
    public static string Summary(IReadOnlyList<ServiceState> installed) =>
        installed.Count == 0 ? "MSI apps: none" : installed.Any(Interferes) ? "MSI apps: active" : "MSI apps: off";

    /// <summary>The hover text: why it matters, then each installed service and what to do.</summary>
    public static string Explain(IReadOnlyList<ServiceState> installed)
    {
        if (installed.Count == 0)
            return "No MSI apps (Dragon Center, MSI Center) are installed, so nothing can override Wyvern Center's settings.";
        var lines = new List<string>
        {
            "Dragon Center and MSI Center can change the fans and performance mode behind Wyvern Center's back. " +
            "For Wyvern Center to work properly, close them and set these services to Disabled " +
            "(Windows Services → service → Startup type: Disabled):",
            "",
        };
        lines.AddRange(installed.Select(s => $"• {s.Name}: {Describe(s)}"));
        lines.Add("");
        lines.Add(installed.Any(Interferes)
            ? "Some are running or start with Windows: they can override your settings."
            : installed.All(s => s.StartMode == ServiceStartMode.Disabled)
                ? "All disabled: nothing can override your settings."
                : "None are running now, but opening Dragon Center or MSI Center starts them again.");
        return string.Join("\n", lines);
    }
}

/// <summary>Reads service state through ServiceController.</summary>
public sealed class WindowsServiceControl : IServiceControl
{
    public ServiceState? Query(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return new ServiceState(name, sc.StartType, sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending);
        }
        catch (InvalidOperationException)
        {
            return null; // not installed
        }
    }
}
