using WyvernCenter.Core.Features;

namespace WyvernCenter.Tests;

public class CpuAndDisplayTests
{
    [Theory]
    [InlineData(CpuBoost.Off, 0u)]
    [InlineData(CpuBoost.Efficient, 4u)]
    [InlineData(CpuBoost.Aggressive, 2u)]
    public void BoostToWindowsValue(CpuBoost boost, uint value) => Assert.Equal(value, WindowsCpuPower.ToPerfBoostMode(boost));

    [Theory]
    [InlineData(0u, CpuBoost.Off)]
    [InlineData(1u, CpuBoost.Aggressive)]
    [InlineData(2u, CpuBoost.Aggressive)]
    [InlineData(3u, CpuBoost.Efficient)]
    [InlineData(4u, CpuBoost.Efficient)]
    [InlineData(5u, CpuBoost.Aggressive)]
    [InlineData(6u, CpuBoost.Efficient)]
    public void WindowsValueToBoost(uint value, CpuBoost boost) => Assert.Equal(boost, WindowsCpuPower.FromPerfBoostMode(value));

    [Fact]
    public void RefreshRates_OnlyForCurrentResolution()
    {
        var modes = new[] { (1920, 1080, 144), (1920, 1080, 60), (1920, 1080, 144), (1280, 720, 120) };
        Assert.Equal(new[] { 60, 144 }, DisplayRefresh.RatesFor(modes, 1920, 1080));
    }
}
