using WyvernCenter.Core.Features;
using WyvernCenter.Core.Monitoring;

namespace WyvernCenter.Tests;

public class OverlayTests
{
    [Fact]
    public void Rpm_FromTach()
    {
        Assert.Equal(6956, FanRpm.FromTach(69, 0));
        Assert.Equal(0, FanRpm.FromTach(0, 0));
        Assert.Equal(1875, FanRpm.FromTach(0, 1));
        Assert.Null(FanRpm.FromTach(null, 0));
    }

    private const long Second = 10_000_000; // event timestamps are FILETIME: 100 ns units

    [Fact]
    public void Fps_FromEventTimestamps()
    {
        var t = new FpsTracker();
        var start = 1_000 * Second;
        for (var i = 0; i < 288; i++) t.Present(42, start + i * Second / 144);   // 2 s at 144 fps
        for (var i = 0; i < 60; i++) t.Present(7, start + i * Second / 30);
        var now = start + 2 * Second;
        Assert.Equal(144, t.Fps(42, now));
        Assert.Equal(30, t.Fps(7, now));
    }

    [Fact]
    public void Fps_BurstyDeliveryDoesNotMatter()
    {
        // ETW hands events over in uneven bursts; only their own timestamps count.
        var t = new FpsTracker();
        var start = 1_000 * Second;
        for (var i = 0; i < 82; i++) t.Present(42, start + i * Second / 144);
        var mid = t.Fps(42, start + Second);
        for (var i = 82; i < 300; i++) t.Present(42, start + i * Second / 144);
        Assert.Equal(144, mid);
        Assert.Equal(144, t.Fps(42, start + 3 * Second));
    }

    [Fact]
    public void Fps_UnknownOrStaleIsNull()
    {
        var t = new FpsTracker();
        var start = 1_000 * Second;
        for (var i = 0; i < 60; i++) t.Present(42, start + i * Second / 60);
        Assert.Null(t.Fps(99, start + Second));                 // never presented: "FPS —"
        Assert.Null(t.Fps(null, start + Second));
        Assert.Null(t.Fps(42, start + 5 * Second));             // stopped presenting a while ago
    }

    private static MonitorSample Sample(int? gpuTemp = 58) => new(DateTime.Now, 63, 71, gpuTemp, 60, 22.4, null, null, null, 3200, 45,
        gpuTemp > 0 ? 1350 : null, gpuTemp > 0 ? 90 : null, gpuTemp > 0 ? 24.2 : null, -12.3, 80, 11.2, 16, 3100, 2900);

    private static OverlayInput In(MonitorSample? s, int? fps = 144, string? app = "game", string? profile = "Balanced", bool idle = false,
        PowerTotal? power = null, IReadOnlyList<double?>? history = null) =>
        new(s, fps is int f ? new FrameStats(f, 1000.0 / f, f) : null, app, profile, idle, power, history);

    [Fact]
    public void Lines_AllParts()
    {
        var l = OverlayText.Lines(In(Sample()), new());
        Assert.Equal("144   1% low 144 · 6.9 ms   game", l[0].Text);
        Assert.True(l[0].Big);
        Assert.Equal("3.20 GHz  45%  22 W  63°C  3100 RPM", l[1].Text);
        Assert.Equal("1350 MHz  90%  24 W  58°C  2900 RPM", l[2].Text);
        Assert.Equal("RAM 11.2 GB  BAT 80% −12.3 W  Balanced", l[3].Text);
        Assert.Equal(new[] { "FPS", "CPU", "GPU", "SYS" }, l.Select(x => x.Label));
    }

    [Fact]
    public void Lines_GpuAsleepOrIdle()
    {
        Assert.Equal("asleep", OverlayText.Lines(In(Sample(gpuTemp: 0), null), new())[2].Text);
        Assert.Equal("idle  60°C", OverlayText.Lines(In(Sample(gpuTemp: 60) with { GpuMhz = null, GpuLoad = null, GpuW = null }, null, idle: true), new())[2].Text);
    }

    [Fact]
    public void Lines_NoFpsAndToggles()
    {
        var l = OverlayText.Lines(In(Sample(), null, profile: "Quiet"), new(Gpu: false, Sys: false));
        Assert.Equal("—", l[0].Text);
        Assert.Equal(new[] { "FPS", "CPU" }, l.Select(x => x.Label));
    }

    [Fact]
    public void Lines_PowerLineBreaksTheTotalDown()
    {
        var power = new PowerTotal(new[]
        {
            new PowerItem(PowerPart.Cpu, "CPU", 20.4, false), new PowerItem(PowerPart.Gpu, "GPU", 24.2, false),
            new PowerItem(PowerPart.Screen, "Screen", 2.1, true), new PowerItem(PowerPart.Keyboard, "Keyboard", 0.2, true),
            new PowerItem(PowerPart.Other, "Other", 4, true),
        }, 50.9, false);
        var l = OverlayText.Lines(In(Sample(), power: power), new());
        var pwr = l.Single(x => x.Label == "PWR");
        Assert.Equal("≈51 W   CPU 20 · GPU 24 · Screen 2 · Other 4", pwr.Text); // parts under 0.5 W left out
        Assert.Equal(OverlayRole.Power, pwr.Role);
    }

    [Fact]
    public void Lines_FpsGraphUnderTheFps_WhenThereIsHistory()
    {
        var l = OverlayText.Lines(In(Sample(), history: new double?[] { 140, null, 144, 138 }), new());
        Assert.Equal(OverlayRole.Graph, l[1].Role);
        Assert.Equal(4, l[1].Graph!.Count);
        Assert.DoesNotContain(OverlayText.Lines(In(Sample(), history: new double?[] { 140 }), new()), x => x.Role == OverlayRole.Graph);
        Assert.DoesNotContain(OverlayText.Lines(In(Sample(), history: new double?[] { 140, 141 }), new(Graph: false)), x => x.Role == OverlayRole.Graph);
    }

    [Fact]
    public void Frames_AverageAndOnePercentLow()
    {
        var t = new FpsTracker();
        long start = 133_000_000_000_000_000, tick = 10_000_000 / 144;
        long time = start;
        for (var i = 0; i < 280; i++) { time += i == 140 ? 500_000 : tick; t.Present(1, time); }  // one 50 ms hitch
        var f = t.Frames(1, time)!;
        Assert.InRange(f.AvgFrameMs, 6.9, 7.2);
        Assert.InRange(f.OnePercentLow!.Value, 15, 40);                                            // the hitch shows in the 1% low
        Assert.InRange(f.Fps, 136, 145);
        Assert.Null(t.Frames(2, time));
    }
}
