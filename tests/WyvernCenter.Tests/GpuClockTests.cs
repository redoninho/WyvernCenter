using WyvernCenter.Core.Features;

namespace WyvernCenter.Tests;

public class GpuClockTests
{
    private sealed class FakeGpu : IGpuClock
    {
        public List<string> Calls { get; } = new();
        public GpuWrite Next { get; set; } = GpuWrite.Ok;
        public GpuWrite SetMaxClock(int mhz) { Calls.Add("set " + mhz); return Next; }
        public GpuWrite ResetClocks() { Calls.Add("reset"); return Next; }
    }

    [Fact]
    public void Asleep_NoCalls()
    {
        var gpu = new FakeGpu();
        var s = new GpuClockState(gpu);
        Assert.False(s.Update(1200, awake: false));
        Assert.Empty(gpu.Calls);
        Assert.False(s.EverAwake);
    }

    [Fact]
    public void WritesOnWakeThenOnlyOnChange()
    {
        var gpu = new FakeGpu();
        var s = new GpuClockState(gpu);
        s.Update(1200, false);
        Assert.True(s.Update(1200, true));
        Assert.False(s.Update(1200, true));
        Assert.True(s.Update(1400, true));
        Assert.Equal(new[] { "set 1200", "set 1400" }, gpu.Calls);
    }

    [Fact]
    public void ReappliesAfterEverySleep()
    {
        var gpu = new FakeGpu();
        var s = new GpuClockState(gpu);
        s.Update(1200, true);
        s.Update(1200, false);
        s.Update(1200, true);
        Assert.Equal(new[] { "set 1200", "set 1200" }, gpu.Calls);
    }

    [Fact]
    public void NoCapResets()
    {
        var gpu = new FakeGpu();
        var s = new GpuClockState(gpu);
        s.Update(null, true);
        Assert.Equal(new[] { "reset" }, gpu.Calls);
    }

    [Fact]
    public void NotSupported_NeverRetried()
    {
        var gpu = new FakeGpu { Next = GpuWrite.NotSupported };
        var s = new GpuClockState(gpu);
        s.Update(1200, true);
        Assert.False(s.Supported);
        Assert.False(s.Update(1400, true));
        s.Update(1400, false);
        Assert.False(s.Update(1400, true));
        Assert.Single(gpu.Calls);
    }

    [Fact]
    public void Failed_NotRetriedUntilWakeOrChange()
    {
        var gpu = new FakeGpu { Next = GpuWrite.Failed };
        var s = new GpuClockState(gpu);
        s.Update(1200, true);
        Assert.Equal(GpuWrite.Failed, s.LastResult);
        Assert.False(s.Update(1200, true));            // no retry every tick while awake
        gpu.Next = GpuWrite.Ok;
        Assert.True(s.Update(1300, true));             // a new cap is tried
        s.Update(1300, false);
        gpu.Next = GpuWrite.Failed;
        s.Update(1300, true);                          // fails again after a wake
        gpu.Next = GpuWrite.Ok;
        s.Update(1300, false);
        Assert.True(s.Update(1300, true));             // and is retried on the next wake
        Assert.Equal(GpuWrite.Ok, s.LastResult);
        Assert.True(s.Supported);
    }

    [Fact]
    public void NoCap_ResetOnlyWhileACapMayBeActive()
    {
        var gpu = new FakeGpu();
        var s = new GpuClockState(gpu);
        s.Update(null, true);                          // unknown state at start: reset once
        s.Update(null, false);
        Assert.False(s.Update(null, true));            // nothing to undo on later wakes
        s.Update(1200, true);
        s.Update(null, true);                          // cap was set: reset
        Assert.Equal(new[] { "reset", "set 1200", "reset" }, gpu.Calls);
        Assert.False(s.CapMayBeActive);
    }

    [Fact]
    public void Forget_WritesSameCapAgain()
    {
        var gpu = new FakeGpu();
        var s = new GpuClockState(gpu);
        s.Update(1200, true);
        s.Forget(); // something else (Restore) reset the clocks
        Assert.True(s.Update(1200, true));
        Assert.Equal(new[] { "set 1200", "set 1200" }, gpu.Calls);
    }

    [Fact]
    public void ResetIfCapped_SkipsWhenNothingIsCapped()
    {
        var gpu = new FakeGpu();
        var s = new GpuClockState(gpu);
        Assert.True(s.ResetIfCapped());                // unknown at start (an earlier session may have left a cap)
        Assert.False(s.ResetIfCapped());               // already reset: don't wake the GPU for nothing
        s.Update(1200, true);
        Assert.True(s.ResetIfCapped());
        Assert.True(s.Update(1200, true));             // the profile's cap goes back on afterwards
        Assert.Equal(new[] { "reset", "set 1200", "reset", "set 1200" }, gpu.Calls);
    }
}
