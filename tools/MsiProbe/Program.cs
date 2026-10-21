using System.Globalization;
using System.Management;
using System.Security.Principal;
using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Scm;
using WyvernCenter.Core.Wmi2;

namespace MsiProbe;

/// <summary>Read-only diagnostics. Nothing in this tool writes to the hardware.</summary>
internal static class Program
{
    private static readonly object SayLock = new();
    private static TextWriter? _log;

    private static int Main(string[] args)
    {
        var rest = args.ToList();
        var logPath = Take(rest, "--log");
        var ms = int.Parse(Take(rest, "--ms") ?? "500", CultureInfo.InvariantCulture);
        var seconds = int.Parse(Take(rest, "--seconds") ?? "60", CultureInfo.InvariantCulture);
        if (logPath is not null) _log = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Say($"--- msi-probe {string.Join(' ', args)} @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (rest.FirstOrDefault() == "sys") return Sys();
        if (rest.FirstOrDefault() == "monitor") return Monitor();
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Say("Run as administrator.");
            return 2;
        }

        try
        {
            return rest.FirstOrDefault() switch
            {
                "dump" => Dump(),
                "guid" => Guid(),
                "wmi2" => Wmi2(),
                "watch" => Watch(ms, seconds),
                "events" => Events(seconds),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Say($"Error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            _log?.Dispose();
        }
    }

    private static int Dump()
    {
        var scm = new WmiScmTransport();
        var id = FirmwareId.Parse(scm.ReadAll(ScmClass.Software));
        Say($"EC firmware: {id}  profile: {ProfileCatalog.Match(FirmwareId.Version(id))?.Name ?? "(none)"}");
        foreach (var c in Enum.GetValues<ScmClass>())
        {
            var v = scm.ReadAll(c);
            Say($"{ScmNames.WmiClass(c),-18} " + string.Join(" ", v.Select((x, i) => $"{i}:{x:X2}")));
        }
        return 0;
    }

    /// <summary>Reads every SCM0 value both ways (MSI's WMI classes and the firmware GUIDs) and reports any difference.</summary>
    private static int Guid()
    {
        using var byGuid = new GuidScmTransport();
        WmiScmTransport? classes = null;
        try { classes = new WmiScmTransport(); classes.ReadAll(ScmClass.System); }
        catch (Exception ex) { Say($"MSI's WMI classes: unavailable ({ex.Message}); GUID route only"); classes = null; }
        var differences = 0;
        foreach (var c in Enum.GetValues<ScmClass>())
        {
            IReadOnlyList<int> g;
            try { g = byGuid.ReadAll(c); }
            catch (Exception ex) { Say($"{ScmNames.WmiClass(c),-18} GUID read failed: {ex.Message}"); differences++; continue; }
            var w = classes?.ReadAll(c);
            var diff = w is null ? new List<int>() : Enumerable.Range(0, g.Count).Where(i => g[i] != w[i]).ToList();
            differences += diff.Count;
            var shown = c is ScmClass.MasterBattery or ScmClass.SlaveBattery ? "(battery values hidden)" : string.Join(" ", g.Select((x, i) => $"{i}:{x:X2}"));
            Say($"{ScmNames.WmiClass(c),-18} {(w is null ? "" : diff.Count == 0 ? "same " : "DIFF ")}{shown}" +
                (diff.Count == 0 ? "" : "  differs at " + string.Join(",", diff.Select(i => $"[{i}] guid {g[i]:X2} / class {w![i]:X2}"))));
        }
        Say(differences == 0 ? "GUID route OK." : $"{differences} difference(s) (values that change on their own, like temperatures, can differ between the two reads).");
        return differences == 0 ? 0 : 1;
    }

    /// <summary>WMI v2 laptops (11th gen+): interface version, EC info, EC RAM and MSI's typed blocks. Reads only.</summary>
    private static int Wmi2()
    {
        using var transport = GuidWmi2Transport.TryOpen(Say);
        if (transport is null) { Say("No WMI v2 interface on this laptop (expected on 10th gen and older)."); return 1; }
        var ec = Wmi2Ec.Detect(transport, Say);
        if (ec is null) return 1;
        var match = ProfileCatalog.Resolve(FirmwareId.Version(ec.Firmware), FirmwareInterface.Wmi2);
        Say($"Instance {transport.Instance}; profile: {match.Profile?.Name ?? "(none)"} ({match.Support})");
        Say(Wmi2Dump.Read(ec).ToMarkdown());
        return 0;
    }

    /// <summary>Prints only what changes: all SCM0 values.</summary>
    private static int Watch(int ms, int seconds)
    {
        var scm = new WmiScmTransport();
        var watched = new[] { ScmClass.System, ScmClass.Cpu, ScmClass.Vga, ScmClass.Device, ScmClass.Power };
        var last = new Dictionary<string, int>();
        var until = DateTime.Now.AddSeconds(seconds);
        Say($"Watching for {seconds}s (Ctrl+C to stop). Change settings in MSI Center now.");
        var first = true;
        while (DateTime.Now < until)
        {
            var now = new Dictionary<string, int>();
            foreach (var c in watched)
            {
                var v = scm.ReadAll(c);
                for (var i = 0; i < v.Count; i++) now[$"{ScmNames.WmiClass(c)}[{i}]"] = v[i];
            }

            var changes = now.Where(kv => !last.TryGetValue(kv.Key, out var old) || old != kv.Value).ToList();
            if (first) Say("initial: " + string.Join("  ", now.Select(kv => $"{kv.Key}={kv.Value:X2}")));
            else foreach (var (k, v) in changes) Say($"{DateTime.Now:HH:mm:ss.fff}  {k,-16} {last[k]:X2} -> {v:X2}");
            last = now;
            first = false;
            Thread.Sleep(ms);
        }
        return 0;
    }

    private static int Events(int seconds)
    {
        using var watcher = new ManagementEventWatcher(new ManagementScope(@"\\.\root\WMI"), new WqlEventQuery("SELECT * FROM MSI_Event"));
        watcher.EventArrived += (_, e) => Say($"{DateTime.Now:HH:mm:ss.fff}  MSIEvt=0x{Convert.ToUInt32(e.NewEvent["MSIEvt"]):X8}");
        watcher.Start();
        Say($"Listening for MSI_Event for {seconds}s. Press the Fn keys now.");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        watcher.Stop();
        return 0;
    }

    /// <summary>Windows-side reads (no admin): CPU boost policy, max processor state, internal panel rates.</summary>
    private static int Sys()
    {
        var cpu = new WyvernCenter.Core.Features.WindowsCpuPower(Path.Combine(Path.GetTempPath(), "unused.json"));
        Say($"CPU boost AC={cpu.GetBoost(true)} DC={cpu.GetBoost(false)}  max state AC={cpu.GetMaxState(true)}% DC={cpu.GetMaxState(false)}%");
        var dev = WyvernCenter.Core.Features.DisplayRefresh.InternalDisplay();
        Say($"Internal display: {dev ?? "(not found)"}  current={(dev is null ? null : WyvernCenter.Core.Features.DisplayRefresh.Current(dev))}  rates={(dev is null ? "" : string.Join(",", WyvernCenter.Core.Features.DisplayRefresh.SupportedRates(dev)))}");
        return 0;
    }

    /// <summary>Windows-side monitoring sources (no admin; EC sensors not included).</summary>
    private static int Monitor()
    {
        using var cpu = new WyvernCenter.Core.Monitoring.WindowsCpuCounters();
        using var gpu = new WyvernCenter.Core.Monitoring.NvmlGpu();
        var m = new WyvernCenter.Core.Monitoring.SystemMonitor(new NoEc(), cpu, gpu, new WyvernCenter.Core.Monitoring.WindowsBattery(), new WyvernCenter.Core.Monitoring.WindowsMemory());
        for (var i = 0; i < 3; i++)
        {
            Thread.Sleep(1000);
            var x = m.Sample();
            Say($"CPU {x.CpuW:F1} W (cores {x.CoresW:F1}, iGPU {x.IgpuW:F1}, DRAM {x.DramW:F1}) {x.CpuMhz:F0} MHz load {x.CpuLoad:F0}% | battery {x.BatteryPct}% {x.BatteryW:F1} W | RAM {x.RamUsedGb:F1}/{x.RamTotalGb:F1} GB");
        }
        var g = gpu.Read();
        Say($"NVML direct (test only): {g?.Mhz} MHz load {g?.Load}% {g?.Watts} W");
        return 0;
    }

    private sealed class NoEc : WyvernCenter.Core.Monitoring.IEcSensors
    {
        public WyvernCenter.Core.Monitoring.EcReading Read() => new(null, null, null, null);
    }

    private static int Usage()
    {
        Say("msi-probe dump | guid | wmi2 | watch [--ms 500] [--seconds 60] | events [--seconds 60]   [--log file]");
        return 3;
    }

    private static string? Take(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        if (i < 0 || i + 1 >= args.Count) return null;
        var v = args[i + 1];
        args.RemoveRange(i, 2);
        return v;
    }

    private static void Say(string line)
    {
        lock (SayLock)
        {
            Console.WriteLine(line);
            _log?.WriteLine(line);
        }
    }
}
