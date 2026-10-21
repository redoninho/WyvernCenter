namespace WyvernCenter.Core.Verify;

/// <summary>Full CPU load: one busy thread per logical processor until stopped.</summary>
public sealed class CpuLoad : IDisposable
{
    private readonly int _threads;
    private volatile bool _stop;
    private readonly List<Thread> _workers = new();

    public CpuLoad(int threads) => _threads = Math.Max(1, threads);
    public bool Running => _workers.Count > 0;

    public void Start()
    {
        if (Running) return;
        _stop = false;
        for (var i = 0; i < _threads; i++)
        {
            var t = new Thread(Spin) { IsBackground = true, Name = "Wyvern Center test load", Priority = ThreadPriority.Normal };
            _workers.Add(t);
            t.Start();
        }
    }

    public void Stop()
    {
        _stop = true;
        foreach (var t in _workers) t.Join(2000);
        _workers.Clear();
    }

    private void Spin()
    {
        double x = 1.0001;
        while (!_stop)
            for (var i = 0; i < 100_000; i++) x = x * 1.0000001 + 0.0000001; // floating-point work the JIT can't drop
        GC.KeepAlive(x);
    }

    public void Dispose() => Stop();
}
