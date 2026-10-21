using System.Text;

namespace WyvernCenter.Core.Wmi2;

/// <summary>
/// Everything a WMI v2 laptop reports, for a support report: interface version, EC info, every EC RAM byte
/// (Get_Data 0x00-0xFF) and the typed blocks MSI Center reads. Reads only. Battery methods are left out (they can
/// hold serial numbers).
/// </summary>
public sealed record Wmi2Dump(
    int Major,
    int Minor,
    Wmi2EcInfo? Ec,
    IReadOnlyList<int?> EcRam,
    IReadOnlyList<(string Name, byte[]? Data)> Blocks)
{
    /// <summary>The typed reads MSI Center makes (see docs/research/wmi2-msi-acpi.md §3).</summary>
    public static readonly IReadOnlyList<(Wmi2Method Method, byte Parameter)> BlockReads = new[]
    {
        (Wmi2Method.GetAp, (byte)0), (Wmi2Method.GetAp, (byte)1),
        (Wmi2Method.GetFan, (byte)0), (Wmi2Method.GetFan, (byte)1), (Wmi2Method.GetFan, (byte)2),
        (Wmi2Method.GetTemperature, (byte)1), (Wmi2Method.GetTemperature, (byte)2),
        (Wmi2Method.GetThermal, (byte)3), (Wmi2Method.GetDevice, (byte)1), (Wmi2Method.GetBios, (byte)1),
        (Wmi2Method.GetPower, (byte)0),
    };

    public static Wmi2Dump Read(Wmi2Ec ec)
    {
        var ram = Enumerable.Range(0, 256).Select(o =>
        {
            try { return (int?)ec.ReadByte(o); }
            catch { return null; }
        }).ToList();
        var blocks = BlockReads.Select(r =>
        {
            byte[]? data;
            try { data = ec.ReadBlock(r.Method, r.Parameter); }
            catch { data = null; }
            return ($"{r.Method}({r.Parameter})", data);
        }).ToList();
        return new Wmi2Dump(ec.Major, ec.Minor, ec.Ec, ram, blocks);
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("### WMI v2 (MSI_ACPI)").AppendLine();
        sb.AppendLine($"- Interface version: {Major}.{Minor}");
        sb.AppendLine(Ec is null
            ? "- Get_EC: no answer"
            : $"- EC: {Ec.Firmware} (layout {Ec.EcRamMainVersion}.{Ec.EcRamSubVersion}, page changed {Ec.EcRamPageChanged}, TigerLake-class {Ec.TigerLakeClass})");
        sb.AppendLine().AppendLine("EC RAM (Get_Data, offset → byte; ?? = refused):").AppendLine().AppendLine("```");
        sb.AppendLine("     " + string.Join(" ", Enumerable.Range(0, 16).Select(i => $"_{i:X}")));
        for (var row = 0; row < 16; row++)
            sb.AppendLine($"{row:X}_   " + string.Join(" ", EcRam.Skip(row * 16).Take(16).Select(v => v is int b ? $"{b:X2}" : "??")));
        sb.AppendLine("```").AppendLine();
        if (EcRam[0xD4] is int fan)
            sb.AppendLine($"Fan mode byte 0xD4 = 0x{fan:X2}: {FanModeText(fan)}").AppendLine();
        sb.AppendLine("Typed blocks (payload after the status byte):").AppendLine().AppendLine("```");
        foreach (var (name, data) in Blocks)
            sb.AppendLine($"{name,-18} " + (data is null ? "refused" : string.Join(" ", data.Select(b => $"{b:X2}"))));
        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>
    /// What the fan mode byte says. A custom curve sets bit 6 in MSI Center's own code but bit 7 per msi-ec, so a
    /// report taken with MSI Center's custom fan mode on settles which one this laptop uses.
    /// </summary>
    public static string FanModeText(int value) => (value & 0xC0) switch
    {
        0x40 => "custom curve, MSI Center's encoding (bit 6)",
        0x80 => "custom curve, msi-ec's encoding (bit 7)",
        0xC0 => "bits 6 and 7 both set (unexpected)",
        _ when (value & 0x10) != 0 => "silent",
        _ => "auto",
    };
}
