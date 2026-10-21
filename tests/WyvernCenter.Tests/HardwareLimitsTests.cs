using WyvernCenter.Core.Features;

namespace WyvernCenter.Tests;

public class HardwareLimitsTests
{
    private static readonly HardwareLimits Gf63 = new(2500, 4500, new GpuLimits("NVIDIA GeForce GTX 1650 with Max-Q Design", 1740, 40));
    private static readonly HardwareLimits Bigger = new(2300, 4700, new GpuLimits("NVIDIA GeForce RTX 3060 Laptop GPU", 1800, 95));

    private static (int? Cpu, int? Gpu, int? W) Of(string id, HardwareLimits hw)
    {
        var p = Presets.Create(id, hw);
        return (p.MaxClockMhz, p.GpuMaxClockMhz, p.GpuPowerW);
    }

    [Fact]
    public void Gf63_PresetsAreExactlyWhatTheyWere()
    {
        Assert.Equal((2000, 900, 15), Of(Presets.Battery, Gf63));
        Assert.Equal((2500, 1100, 20), Of(Presets.Quiet, Gf63));
        Assert.Equal((3500, 1350, 25), Of(Presets.Balanced, Gf63));
        Assert.Equal(((int?)null, 1600, 32), Of(Presets.Performance, Gf63));
        Assert.Equal(((int?)null, (int?)null, (int?)null), Of(Presets.Extreme, Gf63));
        Assert.Equal((1000, 600, 1700, 10), (Gf63.CpuMinMhz, Gf63.GpuMinMhz, Gf63.GpuCapMaxMhz, Gf63.GpuMinW));
    }

    [Fact]
    public void TheFallbackIsTheGf63()
    {
        Assert.Equal(Gf63.CpuBaseMhz, HardwareLimits.Fallback.CpuBaseMhz);
        Assert.Equal(Gf63.CpuMaxMhz, HardwareLimits.Fallback.CpuMaxMhz);
        Assert.Equal(1740, HardwareLimits.Fallback.Gpu!.TopMhz);
        Assert.Equal(40, HardwareLimits.Fallback.Gpu!.MaxW);
    }

    [Fact]
    public void OtherHardware_PresetsScaleToIt()
    {
        Assert.Equal((1800, 950, 36), Of(Presets.Battery, Bigger));
        Assert.Equal((2300, 1150, 48), Of(Presets.Quiet, Bigger));
        Assert.Equal((3500, 1400, 59), Of(Presets.Balanced, Bigger));
        Assert.Equal(((int?)null, 1650, 76), Of(Presets.Performance, Bigger));
        Assert.Equal((1000, 600, 1750, 24), (Bigger.CpuMinMhz, Bigger.GpuMinMhz, Bigger.GpuCapMaxMhz, Bigger.GpuMinW));
    }

    [Fact]
    public void NoNvidiaGpu_PresetsHaveNoGpuCaps()
    {
        var hw = new HardwareLimits(2100, 4200, null);
        Assert.All(Presets.Ids, id => { var p = Presets.Create(id, hw); Assert.Null(p.GpuMaxClockMhz); Assert.Null(p.GpuPowerW); });
        Assert.False(hw.HasGpu);
    }

    [Fact]
    public void SavedProfileBeyondThisMachine_IsClampedToIt()
    {
        var small = new HardwareLimits(2000, 4000, new GpuLimits("GPU", 1500, 35));
        var saved = new Profile { Id = "mine", Name = "Mine", MaxClockMhz = 4300, GpuMaxClockMhz = 1600, GpuPowerW = 60, CpuCurve = Presets.Curve(30, 40, 55, 70, 85, 100), GpuCurve = Presets.Curve(30, 40, 55, 70, 85, 100) };
        var store = new ProfileStore(new[] { saved }, null, null, null, null, small);
        var p = store.Get("mine");
        Assert.Null(p.MaxClockMhz);      // at or above this CPU's top speed = no limit
        Assert.Null(p.GpuMaxClockMhz);
        Assert.Null(p.GpuPowerW);
        Assert.Empty(ProfileStore.Validate(p, null, small));
    }

    [Fact]
    public void NoGpuThisStart_SavedGpuCapsAreKept()
    {
        // e.g. the NVIDIA driver was mid-update at logon: the caps must survive until it's back
        var saved = new Profile { Id = "mine", Name = "Mine", GpuMaxClockMhz = 1350, GpuPowerW = 25, CpuCurve = Presets.Curve(30, 40, 55, 70, 85, 100), GpuCurve = Presets.Curve(30, 40, 55, 70, 85, 100) };
        var store = new ProfileStore(new[] { saved }, null, null, null, null, new HardwareLimits(2500, 4500, null));
        Assert.Equal((1350, 25), (store.Get("mine").GpuMaxClockMhz, store.Get("mine").GpuPowerW));
    }

    [Fact]
    public void CachedGpuLimits_AreOnlyTrustedWhenPlausible()
    {
        Assert.True(WyvernCenter.Core.Monitoring.HardwareDetect.Plausible(new GpuLimits("GPU", 1740, 40)));
        Assert.False(WyvernCenter.Core.Monitoring.HardwareDetect.Plausible(new GpuLimits("GPU", 1740, 2)));
        Assert.False(WyvernCenter.Core.Monitoring.HardwareDetect.Plausible(new GpuLimits("GPU", 100, 40)));
        Assert.False(WyvernCenter.Core.Monitoring.HardwareDetect.Plausible(null));
    }

    [Fact]
    public void HelpTextsAreGeneral_LabelsNameThisMachinesSpeeds()
    {
        Presets.Hardware = HardwareLimits.Fallback; // the default; other tests never change it
        // Help texts are general (no machine's numbers); the labels show this machine's speeds.
        Assert.DoesNotContain("GHz", ProfileText.ClockHelp);
        Assert.DoesNotContain("MHz", ProfileText.GpuHelp);
        Assert.DoesNotContain("GHz", ProfileText.TurboHelp(Turbo.Off));
        Assert.Equal("No limit (4.5 GHz)", ProfileText.ClockLabel(null));
        Assert.Equal("No cap (1740 MHz)", ProfileText.GpuClockLabel(null));
    }
}
