using System.Text;

namespace WyvernCenter.Core.Wmi2;

/// <summary>
/// MSI's "WMI v2" firmware interface (class MSI_ACPI) on 11th-gen Intel and newer MSI laptops: one ACPI WMI GUID
/// with numbered methods, each taking and returning a 32-byte package. Values are the firmware's WmiMethodId numbers.
/// See docs/research/wmi2-msi-acpi.md.
/// </summary>
public enum Wmi2Method : uint
{
    GetPackage = 1, SetPackage = 2,
    GetEc = 3, SetEc = 4,
    GetBios = 5, SetBios = 6,
    GetSmbus = 7, SetSmbus = 8,
    GetMasterBattery = 9, SetMasterBattery = 10,
    GetSlaveBattery = 11, SetSlaveBattery = 12,
    GetTemperature = 13, SetTemperature = 14,
    GetThermal = 15, SetThermal = 16,
    GetFan = 17, SetFan = 18,
    GetDevice = 19, SetDevice = 20,
    GetPower = 21, SetPower = 22,
    GetDebug = 23, SetDebug = 24,
    GetAp = 25, SetAp = 26,
    GetData = 27, SetData = 28,
    GetWmi = 29,
}

/// <param name="Flag">out[0]: 1 = the firmware handled the call.</param>
/// <param name="Data">out[1..]: the payload (MSI's InvokeResults.Data).</param>
public sealed record Wmi2Reply(byte Flag, byte[] Data)
{
    public bool Ok => Flag == 1;
}

/// <param name="TigerLakeClass">Get_EC flag MSI requires before it uses WMI v2 at all.</param>
public sealed record Wmi2EcInfo(bool TigerLakeClass, bool EcRamPageChanged, int EcRamMainVersion, int EcRamSubVersion, string Firmware);

public static class Wmi2Protocol
{
    public static readonly Guid Guid = new("ABBC0F6E-8EA1-11D1-00A0-C90629100000");
    public const int PackageSize = 32;

    /// <summary>[0] = parameter (EC offset for Get/Set_Data, block index for the others), data from [1].</summary>
    public static byte[] Input(byte parameter, ReadOnlySpan<byte> data = default)
    {
        if (data.Length > PackageSize - 1) throw new ArgumentException("At most 31 data bytes.", nameof(data));
        var input = new byte[PackageSize];
        input[0] = parameter;
        data.CopyTo(input.AsSpan(1));
        return input;
    }

    public static Wmi2Reply Parse(byte[] output) =>
        output.Length == 0 ? new Wmi2Reply(0, Array.Empty<byte>()) : new Wmi2Reply(output[0], output[1..]);

    /// <summary>Get_WMI: out[1] major, out[2] minor. MSI treats major ≥ 2 as WMI v2; anything else as absent.</summary>
    public static (int Major, int Minor)? Version(byte[] output) =>
        output.Length > 2 && output[1] >= 2 ? (output[1], output[2]) : null;

    /// <summary>
    /// Get_EC: out[0] = 1 when valid; out[1] = flags (bit 7 TigerLake-class, bit 6 EC RAM page changed, bits 5-4
    /// main / 3-0 sub layout version); out[2..29] = ASCII firmware version + build date + time, packed.
    /// </summary>
    public static Wmi2EcInfo? EcInfo(byte[] output)
    {
        if (output.Length < 30 || output[0] != 1) return null;
        var flags = output[1];
        return new Wmi2EcInfo((flags & 0x80) != 0, (flags & 0x40) != 0, (flags & 0x30) >> 4, flags & 0x0F, FirmwareText(output.AsSpan(2, 28)));
    }

    /// <summary>"1585EMS1.11202232023..." → "1585EMS1.112 02232023 ..." (the same form SCM0 laptops report).</summary>
    public static string FirmwareText(ReadOnlySpan<byte> ascii)
    {
        var text = new string(ascii.ToArray().Where(b => b is >= 0x20 and < 0x7F).Select(b => (char)b).ToArray()).Trim();
        if (text.Length <= 12) return text;
        var sb = new StringBuilder(text[..12]);
        if (text.Length >= 20) sb.Append(' ').Append(text[12..20]);
        else sb.Append(' ').Append(text[12..]);
        if (text.Length > 20) sb.Append(' ').Append(text[20..]);
        return sb.ToString().Trim();
    }
}
