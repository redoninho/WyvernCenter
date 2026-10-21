using WyvernCenter.Core.Features;
using WyvernCenter.Core.Profile;

namespace WyvernCenter.Tests;

public class ProfileStoreTests
{
    private static readonly CurveRules Rules = new(0, 150, 5, 100);
    private static ProfileStore Store(IEnumerable<Profile>? saved = null, string? active = null) => new(saved, active, null, null, Rules);

    [Fact]
    public void Presets_AlwaysPresentInOrder()
    {
        var s = Store(new[] { Presets.Create(Presets.Extreme) });
        Assert.Equal(Presets.Ids, s.Profiles.Select(p => p.Id));
        Assert.All(s.Profiles, p => Assert.True(p.BuiltIn));
    }

    [Fact]
    public void Presets_MatchSpec()
    {
        var b = Presets.Create(Presets.Balanced);
        Assert.Equal(3500, b.MaxClockMhz);
        Assert.Equal(Turbo.Efficient, b.Turbo);
        Assert.Equal(FirmwareLevel.Normal, b.Firmware);
        Assert.Equal(PowerOverlay.Balanced, b.Windows);
        Assert.Equal(new[] { 38, 60, 83, 105, 128, 150 }, b.CpuCurve);
        Assert.Equal(b.CpuCurve, b.GpuCurve);
        Assert.Null(Presets.Create(Presets.Extreme).MaxClockMhz);
        Assert.Equal(FirmwareLevel.SuperBattery, Presets.Create(Presets.Battery).Firmware);
        Assert.All(Presets.Ids.Select(Presets.Create), p => Assert.Empty(ProfileStore.Validate(p, Rules)));
    }

    [Fact]
    public void New_CopiesSelectedAsCustom()
    {
        var s = Store();
        var p = s.New(Presets.Quiet);
        Assert.False(p.BuiltIn);
        Assert.Equal("My profile 1", p.Name);
        Assert.Equal(2500, p.MaxClockMhz);
        Assert.Equal("My profile 2", s.New(Presets.Quiet).Name);
        Assert.Equal(7, s.Profiles.Count);
    }

    [Fact]
    public void Duplicate_NamesCopyUniquely()
    {
        var s = Store();
        Assert.Equal("Balanced copy", s.Duplicate(Presets.Balanced).Name);
        Assert.Equal("Balanced copy 2", s.Duplicate(Presets.Balanced).Name);
    }

    [Fact]
    public void Rename_RulesEnforced()
    {
        var s = Store();
        var p = s.New(Presets.Balanced);
        s.Rename(p.Id, "  Gaming  ");
        Assert.Equal("Gaming", s.Get(p.Id).Name);
        Assert.Throws<ArgumentException>(() => s.Rename(p.Id, " "));
        Assert.Throws<ArgumentException>(() => s.Rename(p.Id, "quiet"));        // case-insensitive clash with a preset name
        Assert.Throws<InvalidOperationException>(() => s.Rename(Presets.Quiet, "Hush"));
    }

    [Fact]
    public void Delete_ActiveAndAutoSwitchFallBack()
    {
        var s = Store();
        var p = s.New(Presets.Performance);
        s.ActiveId = p.Id;
        var s2 = new ProfileStore(s.Profiles, p.Id, p.Id, p.Id, Rules);
        s2.Delete(p.Id);
        Assert.Equal(Presets.Balanced, s2.ActiveId);
        Assert.Equal(Presets.Balanced, s2.OnAc);
        Assert.Equal(Presets.Battery, s2.OnBattery);
        Assert.Throws<InvalidOperationException>(() => s2.Delete(Presets.Balanced));
    }

    [Fact]
    public void Reset_RestoresShippedValues()
    {
        var s = Store();
        s.Edit(Presets.Quiet, p => p.Windows = PowerOverlay.Performance);
        s.Reset(Presets.Quiet);
        Assert.Equal(PowerOverlay.Efficiency, s.Get(Presets.Quiet).Windows);
        var custom = s.New(Presets.Quiet);
        Assert.Throws<InvalidOperationException>(() => s.Reset(custom.Id));
    }

    [Fact]
    public void Edit_InvalidRejectedAndUnchanged()
    {
        var s = Store();
        var errors = s.Edit(Presets.Balanced, p => p.CpuCurve = new[] { 50, 40, 60, 70, 80, 90 });
        Assert.NotEmpty(errors);
        Assert.Equal(Presets.Create(Presets.Balanced).CpuCurve, s.Get(Presets.Balanced).CpuCurve);
        Assert.NotEmpty(s.Edit(Presets.Balanced, p => p.MaxClockMhz = 900));
        Assert.NotEmpty(s.Edit(Presets.Balanced, p => p.MaxClockMhz = 3550));
    }

    [Fact]
    public void Edit_TurboOffClampsClock()
    {
        var s = Store();
        Assert.Empty(s.Edit(Presets.Balanced, p => p.Turbo = Turbo.Off));
        Assert.Equal(2500, s.Get(Presets.Balanced).MaxClockMhz);
        Assert.Empty(s.Edit(Presets.Balanced, p => p.MaxClockMhz = null));
        Assert.Equal(2500, s.Get(Presets.Balanced).MaxClockMhz);
    }

    [Theory]
    [InlineData("Silent", "Balanced", "Silent", "quiet", "balanced", "battery")]
    [InlineData("Performance", "Performance", "Balanced", "performance", "performance", "balanced")]
    [InlineData(null, null, null, null, "balanced", "battery")]
    public void Migrate_MapsOldModes(string? mode, string? ac, string? dc, string? active, string onAc, string onBattery) =>
        Assert.Equal((active, onAc, onBattery), ProfileStore.Migrate(mode, ac, dc));

    [Fact]
    public void JsonRoundTrip_KeepsEverything()
    {
        var opts = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var s = Store();
        s.New(Presets.Extreme);
        var json = System.Text.Json.JsonSerializer.Serialize(s.Profiles, opts);
        var back = System.Text.Json.JsonSerializer.Deserialize<List<Profile>>(json, opts)!;
        var s2 = Store(back);
        Assert.Equal(6, s2.Profiles.Count);
        Assert.Equal(s.Profiles[5].CpuCurve, s2.Profiles[5].CpuCurve);
    }

    [Fact]
    public void Text_DescribesInUnits()
    {
        var b = Presets.Create(Presets.Balanced);
        Assert.Equal("Up to 3.5 GHz · efficient turbo · MSI Normal · Windows balanced", ProfileText.Cpu(b));
        Assert.Equal("Fans 25% → 100%", ProfileText.Fans(b));
        Assert.Equal("No limit (4.5 GHz)", ProfileText.ClockLabel(null));
    }
}
