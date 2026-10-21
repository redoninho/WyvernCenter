using System.ServiceProcess;
using WyvernCenter.Core.Features;

namespace WyvernCenter.Tests;

public class MsiServicesTests
{
    private static readonly string[] Names = { "MSI Foundation Service", "MSI_Central_Service", "Micro Star SCM" };

    [Fact]
    public void OnlyInstalledServicesAreListed()
    {
        var sc = new FakeServices();
        sc.Add("MSI_Central_Service", ServiceStartMode.Disabled, running: false);
        var installed = MsiServices.Installed(sc, Names);
        Assert.Equal(new[] { "MSI_Central_Service" }, installed.Select(s => s.Name));
    }

    [Fact]
    public void RunningOrStartingWithWindows_Interferes()
    {
        Assert.True(MsiServices.Interferes(new ServiceState("a", ServiceStartMode.Manual, Running: true)));
        Assert.True(MsiServices.Interferes(new ServiceState("a", ServiceStartMode.Automatic, Running: false)));
        Assert.False(MsiServices.Interferes(new ServiceState("a", ServiceStartMode.Manual, Running: false)));
        Assert.False(MsiServices.Interferes(new ServiceState("a", ServiceStartMode.Disabled, Running: false)));
    }

    [Fact]
    public void Summary_SaysWhetherMsiAppsCanInterfere()
    {
        Assert.Equal("MSI apps: none", MsiServices.Summary(Array.Empty<ServiceState>()));
        Assert.Equal("MSI apps: off", MsiServices.Summary(new[] { new ServiceState("a", ServiceStartMode.Disabled, false) }));
        Assert.Equal("MSI apps: active", MsiServices.Summary(new[]
        {
            new ServiceState("a", ServiceStartMode.Disabled, false), new ServiceState("b", ServiceStartMode.Automatic, false),
        }));
    }

    [Fact]
    public void Explain_ListsEachServiceAndWhatToDo()
    {
        var text = MsiServices.Explain(new[]
        {
            new ServiceState("MSI Foundation Service", ServiceStartMode.Disabled, false),
            new ServiceState("MSI_Central_Service", ServiceStartMode.Manual, false),
        });
        Assert.Contains("Startup type: Disabled", text);
        Assert.Contains("MSI Foundation Service: disabled", text);
        Assert.Contains("MSI_Central_Service: stopped (starts when an MSI app asks)", text);
        Assert.Contains("opening Dragon Center or MSI Center starts them again", text);
        Assert.Contains("All disabled", MsiServices.Explain(new[] { new ServiceState("x", ServiceStartMode.Disabled, false) }));
        Assert.Contains("No MSI apps", MsiServices.Explain(Array.Empty<ServiceState>()));
    }

    [Fact]
    public void Known_CoversEveryProfilesServices()
    {
        Assert.Contains("MSI Foundation Service", MsiServices.Known);
        Assert.Contains("MSI_Central_Service", MsiServices.Known);
        Assert.Equal(MsiServices.Known.Count, MsiServices.Known.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

public sealed class FakeServices : IServiceControl
{
    public Dictionary<string, ServiceState> States { get; } = new();

    public void Add(string name, ServiceStartMode mode, bool running) => States[name] = new ServiceState(name, mode, running);

    public ServiceState? Query(string name) => States.GetValueOrDefault(name);
}
