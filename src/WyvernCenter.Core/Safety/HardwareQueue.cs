using System.Collections.Concurrent;

namespace WyvernCenter.Core.Safety;

/// <summary>
/// One background thread that runs every hardware job in submission order, one at a time, so multi-step work
/// (a mode = shift + fan + curve + CPU) never interleaves with another action, a slider step or the failsafe.
/// </summary>
public sealed class HardwareQueue : IDisposable
{
    private readonly BlockingCollection<Action> _jobs = new();
    private readonly ConcurrentDictionary<string, byte> _pendingKeys = new();
    private readonly Thread _worker;
    private int _outstanding;
    private readonly ManualResetEventSlim _idle = new(true);

    public HardwareQueue()
    {
        _worker = new Thread(Run) { IsBackground = true, Name = "Wyvern Center hardware" };
        _worker.Start();
    }

    public Task Enqueue(Action work) => Enqueue(() => { work(); return true; });

    public Task<T> Enqueue<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Queues <paramref name="work"/> unless a job with the same key is still waiting or running.</summary>
    public bool TryEnqueueOnce(string key, Action work)
    {
        if (!_pendingKeys.TryAdd(key, 0)) return false;
        Add(() =>
        {
            try { work(); }
            catch { /* periodic jobs report through their own channels */ }
            finally { _pendingKeys.TryRemove(key, out _); }
        });
        return true;
    }

    public bool Busy => Volatile.Read(ref _outstanding) > 0;

    public bool WaitIdle(TimeSpan timeout) => _idle.Wait(timeout);

    private void Add(Action job)
    {
        if (Interlocked.Increment(ref _outstanding) == 1) _idle.Reset();
        try
        {
            _jobs.Add(job);
        }
        catch (InvalidOperationException)
        {
            Done(); // queue shut down
        }
    }

    private void Run()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try { job(); }
            catch { /* each job captures its own result */ }
            finally { Done(); }
        }
    }

    private void Done()
    {
        if (Interlocked.Decrement(ref _outstanding) == 0) _idle.Set();
    }

    public void Dispose()
    {
        _jobs.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(10));
    }
}
