using WyvernCenter.Core.Features;
using WyvernCenter.Core.Profile;

namespace WyvernCenter.Tests;

public class ProfileV32Tests
{
    private static readonly CurveRules Rules = new(0, 150, 5, 100);
    private static ProfileStore Store(IEnumerable<Profile>? saved = null) => new(saved, null, null, null, Rules);

    [Fact]
    public void Presets_GpuPowerAndClocks()
    {
        Assert.Equal((900, 15), (Presets.Create(Presets.Battery).GpuMaxClockMhz, Presets.Create(Presets.Battery).GpuPowerW));
        Assert.Equal((1100, 20), (Presets.Create(Presets.Quiet).GpuMaxClockMhz, Presets.Create(Presets.Quiet).GpuPowerW));
        Assert.Equal((1350, 25), (Presets.Create(Presets.Balanced).GpuMaxClockMhz, Presets.Create(Presets.Balanced).GpuPowerW));
        Assert.Equal((1600, 32), (Presets.Create(Presets.Performance).GpuMaxClockMhz, Presets.Create(Presets.Performance).GpuPowerW));
        Assert.Null(Presets.Create(Presets.Extreme).GpuPowerW);
    }

    [Fact]
    public void Validate_GpuPower()
    {
        var s = Store();
        Assert.NotEmpty(s.Edit(Presets.Balanced, p => p.GpuPowerW = 9));
        Assert.NotEmpty(s.Edit(Presets.Balanced, p => p.GpuPowerW = 41));
        Assert.Empty(s.Edit(Presets.Balanced, p => p.GpuPowerW = 30));
        Assert.Empty(s.Edit(Presets.Balanced, p => p.GpuPowerW = null));
    }

    [Fact]
    public void EveryProfileInCycleByDefault()
    {
        var s = Store();
        var own = s.New(Presets.Balanced);
        Assert.All(s.Profiles, p => Assert.True(p.IsInCycle));
        Assert.Equal(Presets.Battery, s.NextInCycle(own.Id));        // wraps after own profiles
        Assert.Equal(own.Id, s.NextInCycle(Presets.Extreme));        // own profiles come after Extreme
    }

    [Fact]
    public void AtMostTwoOwnProfiles()
    {
        // 5 presets + 2 own + the New/Edit tiles = two rows of four on the main window
        var s = Store();
        s.New(Presets.Balanced); s.Duplicate(Presets.Quiet);
        Assert.False(s.CanAddOwn);
        Assert.Throws<InvalidOperationException>(() => s.New(Presets.Balanced));
        Assert.Throws<InvalidOperationException>(() => s.Duplicate(Presets.Balanced));
    }

    [Fact]
    public void V31ProfilesLoadWithoutPowerLimit()
    {
        var json = """[{"Id":"balanced","Name":"Balanced","GpuMaxClockMhz":1250,"CpuCurve":[42,71,99,125,134,150],"GpuCurve":[21,67,105,133,146,150]}]""";
        var s = Store(System.Text.Json.JsonSerializer.Deserialize<List<Profile>>(json));
        Assert.Null(s.Get(Presets.Balanced).GpuPowerW);
        Assert.Equal(1250, s.Get(Presets.Balanced).GpuMaxClockMhz);
    }

    [Fact]
    public void Text_SummaryAndPower()
    {
        Assert.Equal("CPU up to 3.5 GHz · efficient turbo · GPU up to 1350 MHz · 25 W", ProfileText.Summary(Presets.Create(Presets.Balanced)));
        Assert.Equal("CPU no limit · aggressive turbo · GPU no cap · no power limit", ProfileText.Summary(Presets.Create(Presets.Extreme)));
        Assert.Equal("GPU up to 1350 MHz · 25 W", ProfileText.Gpu(Presets.Create(Presets.Balanced)));
        Assert.Equal("No limit", ProfileText.GpuPowerLabel(null));
        Assert.Equal("25 W", ProfileText.GpuPowerLabel(25));
    }
}
