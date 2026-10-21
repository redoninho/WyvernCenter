using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Scm;

namespace WyvernCenter.Tests;

public class SupportReportTests
{
    private static SupportReport.Info Sample(ModelSupport support = ModelSupport.Generic) => new(
        AppVersion: "0.4.0",
        Manufacturer: "Micro-Star International Co., Ltd.",
        Model: "GF65 Thin 10UE",
        Family: "GF",
        Board: "MS-16W1",
        Bios: "E16W1IMS.10B",
        Cpu: "Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz",
        Gpus: new[] { "NVIDIA GeForce RTX 3060 Laptop GPU", "Intel(R) UHD Graphics" },
        FirmwareId: "16W1EMS1.104 01122021 10:00:00",
        Support: support,
        ProfileName: "MSI laptop (generic, experimental)",
        Scm: new Dictionary<ScmClass, IReadOnlyList<int>?>
        {
            [ScmClass.System] = new[] { 0x01, 0xC1, 0x8D },
            [ScmClass.Cpu] = null,
            [ScmClass.MasterBattery] = new[] { 0x12, 0x34 },
        },
        MsiServices: new[] { ("MSI Foundation Service", "Running") },
        DsdtFile: "dsdt.aml");

    [Fact]
    public void HasWhatAMaintainerNeedsToWriteAProfile()
    {
        var text = SupportReport.Build(Sample());
        Assert.Contains("GF65 Thin 10UE", text);
        Assert.Contains("16W1EMS1.104", text);
        Assert.Contains("Generic", text);
        Assert.Contains("MSI_System", text);
        Assert.Contains("[1] 0xC1", text);
        Assert.Contains("MSI_CPU: unreadable", text);
        Assert.Contains("MSI Foundation Service: Running", text);
        Assert.Contains("dsdt.aml", text);
    }

    [Fact]
    public void LeavesOutBatteryBlocks_TheyCanHoldSerialNumbers()
    {
        var text = SupportReport.Build(Sample());
        Assert.DoesNotContain("Master_Battery", text);
        Assert.DoesNotContain("0x34", text);
    }

    [Fact]
    public void ClassesToDump_SkipBatteries()
    {
        Assert.DoesNotContain(ScmClass.MasterBattery, SupportReport.ClassesToDump);
        Assert.DoesNotContain(ScmClass.SlaveBattery, SupportReport.ClassesToDump);
        Assert.Contains(ScmClass.System, SupportReport.ClassesToDump);
    }
}
