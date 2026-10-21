using WyvernCenter.Core.Features;
using WyvernCenter.Core.Profile;

namespace WyvernCenter.Tests;

public class ProfileGpuCycleTests
{
    private static readonly CurveRules Rules = new(0, 150, 5, 100);
    private static ProfileStore Store(IEnumerable<Profile>? saved = null) => new(saved, null, null, null, Rules);

    [Fact]
    public void Presets_GpuClocksAndCycle()
    {
        Assert.Equal(900, Presets.Create(Presets.Battery).GpuMaxClockMhz);
        Assert.Equal(1100, Presets.Create(Presets.Quiet).GpuMaxClockMhz);
        Assert.Null(Presets.Create(Presets.Extreme).GpuMaxClockMhz);
        Assert.Equal(Presets.Ids, Store().Profiles.Where(p => p.IsInCycle).Select(p => p.Id));
    }

    [Fact]
    public void Validate_GpuClockRange()
    {
        var s = Store();
        Assert.NotEmpty(s.Edit(Presets.Balanced, p => p.GpuMaxClockMhz = 550));
        Assert.NotEmpty(s.Edit(Presets.Balanced, p => p.GpuMaxClockMhz = 1725));
        Assert.NotEmpty(s.Edit(Presets.Balanced, p => p.GpuMaxClockMhz = 1750));
        Assert.Empty(s.Edit(Presets.Balanced, p => p.GpuMaxClockMhz = 1700));
        Assert.Equal(1700, s.Get(Presets.Balanced).GpuMaxClockMhz);
    }

    [Fact]
    public void NewProfilesAreInTheCycle()
    {
        var s = Store();
        Assert.True(s.New(Presets.Balanced).IsInCycle);
        Assert.True(s.Duplicate(Presets.Quiet).IsInCycle);
    }

    [Fact]
    public void V3ProfilesWithoutNewFields_GetDefaults()
    {
        var json = """[{"Id":"quiet","Name":"Quiet","CpuCurve":[30,38,53,75,98,120],"GpuCurve":[30,38,53,75,98,120]},{"Id":"abc","Name":"Mine","CpuCurve":[30,38,53,75,98,120],"GpuCurve":[30,38,53,75,98,120]}]""";
        var saved = System.Text.Json.JsonSerializer.Deserialize<List<Profile>>(json)!;
        var s = Store(saved);
        Assert.True(s.Get(Presets.Quiet).IsInCycle);
        Assert.Null(s.Get(Presets.Quiet).GpuMaxClockMhz);
        Assert.True(s.Get("abc").IsInCycle);
    }

    [Fact]
    public void NextInCycle_WrapsAndStartsFromFirst()
    {
        var s = Store();
        Assert.Equal(Presets.Balanced, s.NextInCycle(Presets.Quiet));
        Assert.Equal(Presets.Battery, s.NextInCycle(Presets.Extreme));  // wraps
        s.SetInCycle(Presets.Battery, false);
        Assert.Equal(Presets.Quiet, s.NextInCycle(Presets.Battery));    // not in the cycle → first in it
        Assert.Equal(Presets.Quiet, s.NextInCycle(null));
    }

    [Fact]
    public void NextInCycle_EmptyIsNull()
    {
        var s = Store();
        foreach (var id in Presets.Ids) s.Edit(id, p => p.InCycle = false);
        Assert.Null(s.NextInCycle(Presets.Balanced));
    }

    [Fact]
    public void Text_Gpu()
    {
        Assert.Equal("GPU up to 1100 MHz · 20 W", ProfileText.Gpu(Presets.Create(Presets.Quiet)));
        Assert.Equal("GPU no cap · no power limit", ProfileText.Gpu(Presets.Create(Presets.Extreme)));
        Assert.Equal("No cap (1740 MHz)", ProfileText.GpuClockLabel(null));
        Assert.Equal("1200 MHz", ProfileText.GpuClockLabel(1200));
    }

}
