using WyvernCenter.Core.Monitoring;
using WyvernCenter.Core.Verify;

namespace WyvernCenter.Tests;

public class ModeMeasurementTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 12, 0, 0);

    private static MonitorSample S(double? w, double? mhz, int? temp = 70) =>
        new(T0, temp, 100, null, null, w, null, null, null, mhz, 100, null, null, null, null, null, null, null);

    /// <summary>One sample a second for a whole phase, starting at <paramref name="from"/> seconds.</summary>
    private static MeasureEvent Feed(ModeMeasurement m, int from, double w, double mhz)
    {
        var last = MeasureEvent.None;
        for (var t = from; t <= from + ModeMeasurement.PhaseSeconds && last == MeasureEvent.None; t++)
            last = m.Sample(S(w, mhz), T0.AddSeconds(t), failsafe: false);
        return last;
    }

    [Fact]
    public void TwoPhases_SwitchInBetween_ThenFinished()
    {
        var m = new ModeMeasurement(T0);
        Assert.Equal(MeasureEvent.SwitchNow, Feed(m, 0, 35, 3800));
        Assert.Equal(new LoadReading(35, 3800, 70), m.Before);

        // Samples while the level is being switched don't count toward either phase.
        Assert.Equal(MeasureEvent.None, m.Sample(S(99, 9999), T0.AddSeconds(16), false));
        m.Switched(T0.AddSeconds(17));
        Assert.Equal(2, m.Phase);
        Assert.Equal(MeasureEvent.Finished, Feed(m, 17, 15, 2400));
        Assert.Equal(new LoadReading(15, 2400, 70), m.After);
        Assert.Equal(3, m.Phase);
    }

    [Fact]
    public void WarmUpIsLeftOutOfTheAverage()
    {
        var m = new ModeMeasurement(T0);
        for (var t = 0; t < ModeMeasurement.WarmupSeconds; t++) m.Sample(S(80, 4500), T0.AddSeconds(t), false);
        var e = MeasureEvent.None;
        for (var t = ModeMeasurement.WarmupSeconds; e == MeasureEvent.None; t++) e = m.Sample(S(30, 3000), T0.AddSeconds(t), false);
        Assert.Equal(30, m.Before!.Watts);
        Assert.Equal(3000, m.Before.Mhz);
    }

    [Theory]
    [InlineData(95, false)]
    [InlineData(70, true)]
    public void TooHotOrFailsafe_StopsAtOnce(int temp, bool failsafe)
    {
        var m = new ModeMeasurement(T0);
        Assert.Equal(MeasureEvent.Stopped, m.Sample(S(40, 4000, temp), T0.AddSeconds(3), failsafe));
        Assert.Equal(3, m.Phase);
        Assert.NotNull(m.StopReason);
        Assert.Equal(MeasureEvent.None, m.Sample(S(40, 4000), T0.AddSeconds(4), false)); // ended once
    }

    [Fact]
    public void Verdict_ClearPowerOrClockDifference_IsYes()
    {
        Assert.Equal(StepAnswer.Yes, ModeMeasurement.Verdict(new(35, 3800, 90), new(15, 2400, 70), "Normal", "Super Battery").Answer);
        Assert.Equal(StepAnswer.Yes, ModeMeasurement.Verdict(new(null, 3800, 90), new(null, 3000, 70), "Normal", "Super Battery").Answer);
        var (answer, summary) = ModeMeasurement.Verdict(new(30, 3000, 80), new(29, 2950, 80), "Normal", "Super Battery");
        Assert.Equal(StepAnswer.No, answer);
        Assert.Contains("Normal 30 W, 3.0 GHz, max 80 °C → Super Battery 29 W", summary);
    }

    [Fact]
    public void Verdict_NotEnoughReadings_IsSkippedNotNo()
    {
        Assert.Equal(StepAnswer.Skipped, ModeMeasurement.Verdict(null, null, "a", "b").Answer);
        Assert.Equal(StepAnswer.Skipped, ModeMeasurement.Verdict(new(null, null, 70), new(null, null, 70), "a", "b").Answer);
        var few = ModeMeasurement.Average(new[] { S(30, 3000), S(30, 3000) }); // below MinSamples
        Assert.Null(few.Watts);
    }
}
