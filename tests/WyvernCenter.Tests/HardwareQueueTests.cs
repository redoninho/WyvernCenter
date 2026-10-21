using WyvernCenter.Core.Safety;

namespace WyvernCenter.Tests;

public class HardwareQueueTests
{
    [Fact]
    public async Task RunsJobsOneAtATimeInOrder()
    {
        using var q = new HardwareQueue();
        var order = new List<int>();
        var running = 0;
        var overlap = false;
        var tasks = Enumerable.Range(0, 20).Select(i => q.Enqueue(() =>
        {
            if (Interlocked.Increment(ref running) > 1) overlap = true;
            Thread.Sleep(2);
            lock (order) order.Add(i);
            Interlocked.Decrement(ref running);
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.False(overlap);
        Assert.Equal(Enumerable.Range(0, 20), order);
    }

    [Fact]
    public async Task ExceptionInOneJobDoesNotStopTheQueue()
    {
        using var q = new HardwareQueue();
        var failed = q.Enqueue(() => throw new InvalidOperationException("boom"));
        var ran = false;
        await q.Enqueue(() => ran = true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        Assert.True(ran);
    }

    [Fact]
    public async Task TryEnqueueOnce_SkipsWhileSameKeyPending()
    {
        using var q = new HardwareQueue();
        var gate = new ManualResetEventSlim();
        var blocker = q.Enqueue(() => gate.Wait());
        var runs = 0;
        Assert.True(q.TryEnqueueOnce("tick", () => Interlocked.Increment(ref runs)));
        Assert.False(q.TryEnqueueOnce("tick", () => Interlocked.Increment(ref runs)));
        gate.Set();
        await blocker;
        await q.Enqueue(() => { });
        Assert.Equal(1, runs);
        Assert.True(q.TryEnqueueOnce("tick", () => { })); // allowed again once the first ran
    }

    [Fact]
    public async Task WaitIdle_ReturnsAfterQueuedWork()
    {
        using var q = new HardwareQueue();
        var done = false;
        _ = q.Enqueue(() => { Thread.Sleep(30); done = true; });
        Assert.True(q.WaitIdle(TimeSpan.FromSeconds(5)));
        Assert.True(done);
        await Task.CompletedTask;
    }
}
