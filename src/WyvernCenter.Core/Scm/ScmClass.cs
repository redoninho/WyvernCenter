namespace WyvernCenter.Core.Scm;

/// <summary>The nine SCM0 WMI data blocks the firmware exposes (DSDT device SCM0, object IDs AA–AI).</summary>
public enum ScmClass { System, Cpu, Vga, Device, Power, Software, Ap, MasterBattery, SlaveBattery }

public static class ScmNames
{
    private const string InstancePrefix = @"ACPI\PNP0C14\0_";

    public static string WmiClass(ScmClass c) => "MSI_" + Property(c);

    public static string Property(ScmClass c) => c switch
    {
        ScmClass.System => "System",
        ScmClass.Cpu => "CPU",
        ScmClass.Vga => "VGA",
        ScmClass.Device => "Device",
        ScmClass.Power => "Power",
        ScmClass.Software => "Software",
        ScmClass.Ap => "AP",
        ScmClass.MasterBattery => "Master_Battery",
        ScmClass.SlaveBattery => "Slave_Battery",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>Instance counts from the firmware's _WDG table.</summary>
    public static int Count(ScmClass c) => c switch
    {
        ScmClass.System => 21,
        ScmClass.Cpu => 19,
        ScmClass.Vga => 18,
        ScmClass.Device => 4,
        ScmClass.Power => 3,
        ScmClass.Software => 34,
        ScmClass.Ap => 8,
        ScmClass.MasterBattery => 16,
        ScmClass.SlaveBattery => 14,
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>The firmware's WMI GUID for each block (SCM0 _WDG), for access without MSI's class definitions.</summary>
    public static Guid FirmwareGuid(ScmClass c) => c switch
    {
        ScmClass.System => new Guid("A1753D7B-B621-DE4A-B41A-55716A0ECE7A"),
        ScmClass.Cpu => new Guid("BD2A216F-2FB9-A640-B807-DDDBAD656891"),
        ScmClass.Vga => new Guid("1EC3EC7A-1E9B-E74A-9026-CF122B0BBD21"),
        ScmClass.Device => new Guid("4AFBD56B-9F91-8F49-81F5-995BA73822AF"),
        ScmClass.Power => new Guid("2D3CBA6C-1C9C-7F41-B54C-F5D5D580D482"),
        ScmClass.Software => new Guid("24418D6A-0A79-524C-9AB1-18B78CA68CE7"),
        ScmClass.Ap => new Guid("A1753D7C-B621-DE4A-B41A-55716A0ECE7A"),
        ScmClass.MasterBattery => new Guid("40BA026D-075D-CD4A-9710-F7C57347CAC9"),
        ScmClass.SlaveBattery => new Guid("8DBCCF6E-9DB4-0E46-A3F2-99AFAAA77A7A"),
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>Battery blocks hold a signed 16-bit value per instance; the rest one byte.</summary>
    public static int ValueSize(ScmClass c) => c is ScmClass.MasterBattery or ScmClass.SlaveBattery ? 2 : 1;

    public static string InstanceName(int index) => InstancePrefix + index;

    public static int InstanceIndex(string instanceName) =>
        int.Parse(instanceName[(instanceName.LastIndexOf('_') + 1)..]);
}
