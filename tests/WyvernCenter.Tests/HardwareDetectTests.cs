using WyvernCenter.Core.Features;
using WyvernCenter.Core.Monitoring;

namespace WyvernCenter.Tests;

public class HardwareDetectTests
{
    [Fact]
    public void CpuidFrequencies_AreUsedAsTheyAre()
    {
        var hw = HardwareDetect.Combine(cpuid: (2500, 4500), wmiBaseMhz: 2496, gpu: null);
        Assert.Equal((2500, 4500), (hw.CpuBaseMhz, hw.CpuMaxMhz));
        Assert.Null(hw.Gpu);
    }

    [Fact]
    public void WithoutCpuid_BaseComesFromWindowsAndTopIsEstimated()
    {
        var hw = HardwareDetect.Combine(cpuid: null, wmiBaseMhz: 2496, gpu: null);
        Assert.Equal((2500, 4500), (hw.CpuBaseMhz, hw.CpuMaxMhz));
    }

    [Fact]
    public void ImplausibleReadings_FallBackToDefaults()
    {
        var hw = HardwareDetect.Combine(cpuid: (0, 0), wmiBaseMhz: 0, gpu: null);
        Assert.Equal((HardwareLimits.Fallback.CpuBaseMhz, HardwareLimits.Fallback.CpuMaxMhz), (hw.CpuBaseMhz, hw.CpuMaxMhz));
        var odd = HardwareDetect.Combine(cpuid: (3000, 2000), wmiBaseMhz: null, gpu: null); // top below base
        Assert.True(odd.CpuMaxMhz >= odd.CpuBaseMhz);
    }

    [Fact]
    public void GpuPower_PrefersTheHighestLimitTheDriverAllows()
    {
        Assert.Equal(40, HardwareDetect.GpuMaxWatts(defaultMw: 35000, constraintMaxMw: 40000, enforcedMw: 40000));
        Assert.Equal(35, HardwareDetect.GpuMaxWatts(defaultMw: 35000, constraintMaxMw: null, enforcedMw: null));
        Assert.Null(HardwareDetect.GpuMaxWatts(null, null, null));
        Assert.Equal(115, HardwareDetect.GpuMaxWatts(defaultMw: 80000, constraintMaxMw: 115000, enforcedMw: 80000));
    }
}

public class NativeLibrariesTests
{
    [Fact]
    public void NvmlIsOnlyEverLoadedFromAdminOnlyFolders()
    {
        var trusted = new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        Assert.All(WyvernCenter.Core.NativeLibraries.NvmlPaths, p =>
        {
            Assert.True(Path.IsPathFullyQualified(p));
            Assert.Contains(trusted, t => p.StartsWith(t + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        });
    }
}
