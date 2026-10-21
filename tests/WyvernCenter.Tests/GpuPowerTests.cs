using WyvernCenter.Core.Features;
using WyvernCenter.Core.Monitoring;

namespace WyvernCenter.Tests;

public class GpuPowerTests
{
    [Fact]
    public void Limiter_StepsDownWhenOver()
    {
        var l = new GpuPowerLimiter();
        Assert.Equal(1300, l.Step(25, 1350, 27));      // 2 W over → −50
        Assert.Equal(1150, l.Step(25, 1350, 31));      // > 5 W over → −150
    }

    [Fact]
    public void Limiter_StepsUpToCeilingWhenUnder()
    {
        var l = new GpuPowerLimiter();
        l.Step(25, 1350, 31);                           // 1200
        Assert.Equal(1250, l.Step(25, 1350, 20));
        Assert.Equal(1300, l.Step(25, 1350, 20));
        Assert.Equal(1350, l.Step(25, 1350, 20));
        Assert.Equal(1350, l.Step(25, 1350, 20));       // never above the profile's max clock
    }

    [Fact]
    public void Limiter_HoldsInsideBandAndRespectsFloor()
    {
        var l = new GpuPowerLimiter();
        Assert.Equal(1350, l.Step(25, 1350, 24));       // within −2 … +0.5: hold
        for (var i = 0; i < 20; i++) l.Step(10, 1350, 40);
        Assert.Equal(GpuPowerLimiter.FloorMhz, l.Cap);
    }

    [Fact]
    public void Limiter_NoTargetMeansProfileCap()
    {
        var l = new GpuPowerLimiter();
        l.Step(25, 1350, 31);
        Assert.Equal(1350, l.Step(null, 1350, 31));
        Assert.Null(l.Cap);
        Assert.Null(l.Step(null, null, 31));            // no cap at all
    }

    [Fact]
    public void Limiter_NoCeilingStartsAtTopAndTopMeansNoCap()
    {
        var l = new GpuPowerLimiter();
        Assert.Null(l.Step(32, null, 30));              // under target at top speed: no cap
        Assert.Equal(Presets.GpuTopMhz - 50, l.Step(32, null, 33));
    }

    [Fact]
    public void Limiter_EffectiveAndReset()
    {
        var l = new GpuPowerLimiter();
        l.Step(25, 1350, 31);
        Assert.Equal(1200, l.Effective(1350, 25));
        Assert.Equal(1100, l.Effective(1100, 25));      // a lower profile cap wins
        Assert.Equal(1350, l.Effective(1350, null));
        l.Reset();
        Assert.Equal(1350, l.Effective(1350, 25));
    }

    private static GpuReading Idle => new(300, 0, 3.5);
    private static GpuReading Busy => new(1300, 90, 24);

    [Fact]
    public void Policy_NeverPollsAsleep()
    {
        var p = new GpuPollPolicy();
        Assert.False(p.ShouldPoll(0));
        Assert.False(p.ShouldPoll(null));
        Assert.True(p.ShouldPoll(50));
    }

    [Fact]
    public void Policy_BacksOffAfterThreeIdleReadings()
    {
        var p = new GpuPollPolicy();
        for (var i = 0; i < 3; i++) { Assert.True(p.ShouldPoll(50)); p.Report(Idle, 50); }
        Assert.True(p.BackedOff);
        Assert.False(p.ShouldPoll(51));
    }

    [Fact]
    public void Policy_BusyKeepsPolling()
    {
        var p = new GpuPollPolicy();
        for (var i = 0; i < 10; i++) { p.ShouldPoll(60); p.Report(i % 3 == 2 ? Busy : Idle, 60); }
        Assert.False(p.BackedOff);
    }

    [Fact]
    public void Policy_ResumesOnTemperatureRiseOrAfterSleep()
    {
        var p = new GpuPollPolicy();
        for (var i = 0; i < 3; i++) { p.ShouldPoll(50); p.Report(Idle, 50); }
        Assert.False(p.ShouldPoll(52));
        Assert.True(p.ShouldPoll(53));                  // +3 °C: real work
        for (var i = 0; i < 3; i++) { p.ShouldPoll(53); p.Report(Idle, 53); }
        Assert.False(p.ShouldPoll(53));
        Assert.False(p.ShouldPoll(0));                  // slept
        Assert.True(p.ShouldPoll(49));                  // woke again: poll
    }

    [Fact]
    public void Limiter_StepsFromTheClockActuallyRunning()
    {
        var l = new GpuPowerLimiter();
        // Cap 1600 but the GPU only runs 1400: the first step must bite at once.
        Assert.Equal(1350, l.Step(32, 1600, 34, 1400));
        Assert.Equal(1350, l.Step(32, 1600, 31.5, 1340));   // inside the band: hold
    }

    [Fact]
    public void Policy_ResumesFromTheCoolestTemperatureSeen()
    {
        var p = new GpuPollPolicy();
        for (var i = 0; i < 3; i++) { p.ShouldPoll(60); p.Report(Idle, 60); }
        Assert.False(p.ShouldPoll(45));                     // cooled while idle (still awake)
        Assert.True(p.ShouldPoll(48));                      // a new game warms it 3 °C from there
    }

    private sealed class FakeReader : IGpuReader
    {
        public Queue<GpuReading?> Next { get; } = new();
        public int Reads, Releases;
        public GpuReading? Read() { Reads++; return Next.Count > 0 ? Next.Dequeue() : Busy; }
        public void Release() => Releases++;
    }

    [Fact]
    public void Watcher_NotNeeded_ReleasesOnceAndClears()
    {
        var gpu = new FakeReader();
        var w = new GpuWatcher(gpu);
        w.Tick(needed: true, ecTemp: 60, targetW: 25, ceilingMhz: 1350);
        Assert.True(w.Polling);
        Assert.NotNull(w.Last);
        w.Tick(needed: false, ecTemp: 60, targetW: null, ceilingMhz: null);
        Assert.False(w.Polling);
        Assert.Null(w.Last);
        Assert.Equal(1, gpu.Releases);
        w.Tick(needed: false, ecTemp: 60, targetW: null, ceilingMhz: null);
        Assert.Equal(2, gpu.Releases);                      // always released: a write elsewhere may have opened NVML
        Assert.Equal(1, gpu.Reads);
    }

    [Fact]
    public void Watcher_Asleep_NoReadsAndReleased()
    {
        var gpu = new FakeReader();
        var w = new GpuWatcher(gpu);
        Assert.False(w.Tick(true, 0, 25, 1350));
        Assert.Equal(0, gpu.Reads);
        Assert.True(gpu.Releases >= 1);
        Assert.Null(w.Last);
    }

    [Fact]
    public void Watcher_OverTarget_MovesTheLimiter()
    {
        var gpu = new FakeReader();
        gpu.Next.Enqueue(new GpuReading(1350, 95, 31));
        var w = new GpuWatcher(gpu);
        Assert.True(w.Tick(true, 70, 25, 1350));
        Assert.Equal(1200, w.Limiter.Cap);
    }

    [Fact]
    public void Watcher_IdleBackOffReleases()
    {
        var gpu = new FakeReader();
        for (var i = 0; i < 3; i++) gpu.Next.Enqueue(Idle);
        var w = new GpuWatcher(gpu);
        for (var i = 0; i < 3; i++) w.Tick(true, 50, 25, 1350);
        Assert.True(w.Policy.BackedOff);
        Assert.False(w.Polling);
        Assert.True(gpu.Releases >= 1);
        var reads = gpu.Reads;
        w.Tick(true, 50, 25, 1350);
        Assert.Equal(reads, gpu.Reads);                     // backed off: no more questions
    }

    [Fact]
    public void Watcher_FailedReadReleasesSoNvmlRetries()
    {
        var gpu = new FakeReader();
        gpu.Next.Enqueue(null);
        var w = new GpuWatcher(gpu);
        w.Tick(true, 60, 25, 1350);
        Assert.Equal(1, gpu.Releases);
    }

    [Fact]
    public void Policy_IdleMeansNoLoadEvenAtHighIdleWatts()
    {
        // This GTX 1650 can sit in P0 at ~15 W with 0 % load; that is still idle, so stop asking and let it sleep.
        var p = new GpuPollPolicy();
        for (var i = 0; i < 3; i++) { p.ShouldPoll(48); p.Report(new GpuReading(990, 0, 15), 48); }
        Assert.True(p.BackedOff);
    }
}
