using WyvernCenter.Core.Features;
using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Safety;
using WyvernCenter.Core.Scm;

namespace WyvernCenter.Tests;

public class FanCurveTests
{
    private static readonly CurveRules Rules = new(0, 150, 5, 100);

    [Fact]
    public void Validate_NonDecreasingCurveOk() =>
        Assert.Empty(FanCurveRules.Validate(new[] { 38, 92, 115, 128, 142, 150 }, Rules));

    [Fact]
    public void Validate_DecreasingRejected() =>
        Assert.Contains(FanCurveRules.Validate(new[] { 38, 92, 80, 128, 142, 150 }, Rules), e => e.Contains("rise"));

    [Theory]
    [InlineData(-1)]
    [InlineData(151)]
    public void Validate_OutOfRangeRejected(int bad) =>
        Assert.NotEmpty(FanCurveRules.Validate(new[] { bad, 100, 100, 100, 150, 150 }.Select(v => v == bad ? bad : Math.Max(v, bad)).ToArray(), Rules));

    [Fact]
    public void Validate_FloorRuleRejectsQuietHotPoint() =>
        Assert.Contains(FanCurveRules.Validate(new[] { 0, 10, 20, 30, 40, 50 }, Rules), e => e.Contains("at least 100"));

    [Fact]
    public void Validate_WrongPointCountRejected() =>
        Assert.NotEmpty(FanCurveRules.Validate(new[] { 50, 100, 150 }, Rules));

    [Fact]
    public void Max_IsAllPointsAtMaxSpeed() => Assert.All(FanCurveRules.Max(Rules), v => Assert.Equal(150, v));
}

public class ThermalFailsafeTests
{
    [Fact]
    public void SingleSpikeIgnored()
    {
        var f = new ThermalFailsafe();
        Assert.Equal(FailsafeAction.None, f.Update(96, 50));
        Assert.Equal(FailsafeAction.None, f.Update(80, 50));
        Assert.False(f.Engaged);
    }

    [Fact]
    public void TwoConsecutiveHotSamplesEngage()
    {
        var f = new ThermalFailsafe();
        f.Update(95, 50);
        Assert.Equal(FailsafeAction.Engage, f.Update(50, 97));
        Assert.True(f.Engaged);
    }

    [Fact]
    public void NoReengageWhileEngaged()
    {
        var f = new ThermalFailsafe();
        f.Update(99, 0);
        f.Update(99, 0);
        Assert.Equal(FailsafeAction.None, f.Update(99, 0));
    }

    [Fact]
    public void ReleaseNeedsBothBelow85()
    {
        var f = new ThermalFailsafe();
        f.Update(99, 0);
        f.Update(99, 0);
        Assert.Equal(FailsafeAction.None, f.Update(84, 86));
        Assert.Equal(FailsafeAction.Release, f.Update(84, 60));
        Assert.False(f.Engaged);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(null, 40)]
    public void Engaged_UnknownCpuReadingNeverReleases(int? cpu, int? gpu)
    {
        var f = new ThermalFailsafe();
        f.Update(99, 0);
        f.Update(99, 0);
        Assert.Equal(FailsafeAction.None, f.Update(cpu, gpu));
        Assert.True(f.Engaged);
    }

    [Fact]
    public void Engaged_ReleasesWhenCpuValidAndCoolAndGpuOff()
    {
        var f = new ThermalFailsafe();
        f.Update(99, 0);
        f.Update(99, 0);
        Assert.Equal(FailsafeAction.Release, f.Update(70, null));
    }

    [Fact]
    public void InvalidReadingsIgnored()
    {
        var f = new ThermalFailsafe();
        f.Update(0, 0);
        Assert.Equal(FailsafeAction.None, f.Update(255, 255)); // bogus sensor bytes are not heat
    }
}

public sealed class FakeCpuPower : ICpuPower
{
    public CpuBoost? Boost { get; private set; }
    public int MaxState { get; private set; } = 100;
    public int MaxFrequency { get; private set; } = -1;
    public bool ThrowOnSave { get; set; }
    public CpuBoost GetBoost(bool ac) => Boost ?? CpuBoost.Aggressive;
    public void SetBoost(CpuBoost boost) => Boost = boost;
    public int GetMaxState(bool ac) => MaxState;
    public void SetMaxState(bool ac, int percent) => MaxState = percent;
    public int GetMaxFrequency(bool ac) => MaxFrequency;
    public void SetMaxFrequency(int mhz) => MaxFrequency = mhz;
    public void SaveOriginalsIfMissing()
    {
        if (ThrowOnSave) throw new IOException("data folder not writable");
    }
    public void RestoreOriginals() { }
}
