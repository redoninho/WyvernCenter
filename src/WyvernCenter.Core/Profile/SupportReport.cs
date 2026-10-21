using System.Runtime.InteropServices;
using System.Text;
using WyvernCenter.Core.Scm;
using WyvernCenter.Core.Wmi2;

namespace WyvernCenter.Core.Profile;

/// <summary>
/// The text a laptop owner attaches to a "support my model" request: what the machine is, its EC firmware, and every
/// value its MSI firmware interface (SCM0) reports. Battery blocks are left out (they can hold serial numbers); no
/// serial numbers, user names or paths are included.
/// </summary>
public static class SupportReport
{
    public sealed record Info(
        string AppVersion,
        string Manufacturer,
        string Model,
        string Family,
        string Board,
        string Bios,
        string Cpu,
        IReadOnlyList<string> Gpus,
        string FirmwareId,
        ModelSupport Support,
        string? ProfileName,
        IReadOnlyDictionary<ScmClass, IReadOnlyList<int>?> Scm,
        IReadOnlyList<(string Name, string Status)> MsiServices,
        string? DsdtFile,
        Wmi2Dump? Wmi2 = null,
        string? VerifyMarkdown = null);

    public static readonly IReadOnlyList<ScmClass> ClassesToDump =
        Enum.GetValues<ScmClass>().Where(c => c is not (ScmClass.MasterBattery or ScmClass.SlaveBattery)).ToList();

    public static string Build(Info i)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Wyvern Center support report").AppendLine();
        sb.AppendLine($"- Wyvern Center: {i.AppVersion}");
        sb.AppendLine($"- Laptop: {i.Manufacturer} {i.Model} (family {i.Family}, board {i.Board})");
        sb.AppendLine($"- BIOS: {i.Bios}");
        sb.AppendLine($"- CPU: {i.Cpu}");
        sb.AppendLine($"- GPU: {string.Join(" + ", i.Gpus)}");
        sb.AppendLine($"- EC firmware: {(i.FirmwareId == "" ? "(not readable)" : i.FirmwareId)}");
        sb.AppendLine($"- Support: {i.Support}{(i.ProfileName is null ? "" : $" (profile: {i.ProfileName})")}");
        foreach (var (name, status) in i.MsiServices) sb.AppendLine($"- {name}: {status}");
        if (i.DsdtFile is not null) sb.AppendLine($"- ACPI DSDT table saved as {i.DsdtFile} (attach it too: it maps each SCM0 value to an EC address)");
        sb.AppendLine();
        // A WMI v2 laptop has no SCM0: its section replaces the (all unreadable) SCM0 one.
        if (i.Wmi2 is null || i.Scm.Values.Any(v => v is not null))
        {
            sb.AppendLine("### SCM0 values (index → raw byte)").AppendLine().AppendLine("```");
            foreach (var (c, values) in i.Scm.Where(kv => ClassesToDump.Contains(kv.Key)).OrderBy(kv => kv.Key))
            {
                var name = ScmNames.WmiClass(c);
                if (values is null) { sb.AppendLine($"{name}: unreadable"); continue; }
                sb.AppendLine($"{name}:");
                for (var start = 0; start < values.Count; start += 8)
                    sb.AppendLine("  " + string.Join("  ", values.Skip(start).Take(8).Select((v, k) => $"[{start + k}] 0x{v:X2}")));
            }
            sb.AppendLine("```").AppendLine();
        }
        if (i.Wmi2 is not null) sb.Append(i.Wmi2.ToMarkdown()).AppendLine();
        sb.AppendLine(i.VerifyMarkdown ?? "\"Verify my laptop\" hasn't been run yet (Settings → This laptop).");
        sb.AppendLine("To help confirm settings, also note what MSI Center's modes do on this laptop (see docs/adding-a-model.md).");
        return sb.ToString();
    }

    /// <summary>The ACPI DSDT table (raw AML), or null. Contains the firmware's code, no personal data.</summary>
    public static byte[]? ReadDsdt()
    {
        const uint Acpi = 0x41435049, Dsdt = 0x54445344; // 'ACPI', 'DSDT' (little-endian table signature)
        try
        {
            var size = GetSystemFirmwareTable(Acpi, Dsdt, null, 0);
            if (size == 0) return null;
            var buffer = new byte[size];
            return GetSystemFirmwareTable(Acpi, Dsdt, buffer, size) == size ? buffer : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);
}
