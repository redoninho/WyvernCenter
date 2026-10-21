using WyvernCenter.Core.Scm;

namespace WyvernCenter.Tests;

public class ScmTests
{
    [Theory]
    [InlineData(ScmClass.System, 21)]
    [InlineData(ScmClass.Cpu, 19)]
    [InlineData(ScmClass.Vga, 18)]
    [InlineData(ScmClass.Device, 4)]
    [InlineData(ScmClass.Power, 3)]
    [InlineData(ScmClass.Software, 34)]
    [InlineData(ScmClass.Ap, 8)]
    [InlineData(ScmClass.MasterBattery, 16)]
    [InlineData(ScmClass.SlaveBattery, 14)]
    public void Count_MatchesFirmwareWdg(ScmClass c, int expected) => Assert.Equal(expected, ScmNames.Count(c));

    [Theory]
    [InlineData(ScmClass.System, "MSI_System", "System")]
    [InlineData(ScmClass.Cpu, "MSI_CPU", "CPU")]
    [InlineData(ScmClass.Vga, "MSI_VGA", "VGA")]
    [InlineData(ScmClass.MasterBattery, "MSI_Master_Battery", "Master_Battery")]
    [InlineData(ScmClass.Ap, "MSI_AP", "AP")]
    public void Names_MatchWmiClasses(ScmClass c, string wmiClass, string property)
    {
        Assert.Equal(wmiClass, ScmNames.WmiClass(c));
        Assert.Equal(property, ScmNames.Property(c));
    }

    [Theory]
    [InlineData(@"ACPI\PNP0C14\0_7", 7)]
    [InlineData(@"ACPI\PNP0C14\0_33", 33)]
    public void InstanceIndex_ParsesSuffix(string name, int expected) => Assert.Equal(expected, ScmNames.InstanceIndex(name));

    [Fact]
    public void InstanceName_ForIndex() => Assert.Equal(@"ACPI\PNP0C14\0_9", ScmNames.InstanceName(9));
}
