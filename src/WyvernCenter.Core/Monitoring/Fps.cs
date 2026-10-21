using System.Runtime.InteropServices;

namespace WyvernCenter.Core.Monitoring;

/// <summary>Frame rate, average frame time and 1% low (the average FPS of the slowest 1% of frames: stutter).</summary>
public sealed record FrameStats(int Fps, double AvgFrameMs, int? OnePercentLow);

/// <summary>
/// Frame rate per process from the timestamps of its Present events (fed by <see cref="EtwPresentSession"/>). ETW
/// hands events over in uneven bursts, so the rate comes from the events' own times, never from when they arrived.
/// A process that hasn't presented for a few seconds no longer counts. Thread-safe.
/// </summary>
public sealed class FpsTracker
{
    /// <summary>FILETIME units (100 ns): rate over the last 2 s; a process silent for 3 s isn't presenting.</summary>
    public const long WindowTicks = 2 * 10_000_000, StaleTicks = 3 * 10_000_000;

    private sealed class Track
    {
        public readonly Queue<long> Times = new();
        public long Last;
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, Track> _tracks = new();

    public void Present(int pid, long timestamp)
    {
        lock (_lock)
        {
            if (!_tracks.TryGetValue(pid, out var t)) _tracks[pid] = t = new Track();
            t.Times.Enqueue(timestamp);
            if (timestamp > t.Last) t.Last = timestamp;
            while (t.Times.Count > 0 && t.Times.Peek() < t.Last - WindowTicks) t.Times.Dequeue();
        }
    }

    /// <summary>FPS plus frame-time stats over the last 2 s; null like <see cref="Fps"/>.</summary>
    public FrameStats? Frames(int? pid, long now)
    {
        if (Fps(pid, now) is not int fps) return null;
        long[] times;
        lock (_lock) times = _tracks[pid!.Value].Times.ToArray();
        var gaps = new double[times.Length - 1];
        for (var i = 1; i < times.Length; i++) gaps[i - 1] = (times[i] - times[i - 1]) / 1e4; // ms
        var avg = gaps.Average();
        int? low = null;
        if (gaps.Length >= 10)
        {
            var worst = gaps.OrderByDescending(g => g).Take(Math.Max(1, gaps.Length / 100)).Average();
            low = worst > 0 ? (int)Math.Round(1000 / worst) : null;
        }
        return new FrameStats(fps, avg, low);
    }

    /// <param name="now">Current time as FILETIME (UTC), the same clock as the event timestamps.</param>
    /// <returns>Null when that process isn't presenting frames (desktop, idle app): the overlay shows "—".</returns>
    public int? Fps(int? pid, long now)
    {
        lock (_lock)
        {
            foreach (var gone in _tracks.Where(kv => kv.Value.Last < now - 2 * StaleTicks).Select(kv => kv.Key).ToList())
                _tracks.Remove(gone);
            if (pid is not int p || !_tracks.TryGetValue(p, out var t) || t.Times.Count < 2 || t.Last < now - StaleTicks) return null;
            var span = t.Last - t.Times.Peek();
            return span > 0 ? (int)Math.Round((t.Times.Count - 1) * 1e7 / span) : null;
        }
    }
}

/// <summary>
/// A real-time ETW session that sees every DXGI (DX10–12) and D3D9 Present call on the system, so FPS can be
/// measured without injecting anything into games (the approach of Intel's PresentMon). Needs administrator
/// rights. The session outlives a crashed process, so <see cref="Start"/> first stops a stale one of the same name.
/// </summary>
public sealed class EtwPresentSession : IDisposable
{
    public const string SessionName = "WyvernCenter-FPS";
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const ushort DxgiPresentStart = 42, D3d9PresentStart = 1;

    // EVENT_TRACE_PROPERTIES (x64): 120 bytes, then room for the logger name.
    private const int PropertiesSize = 120, PropertiesBuffer = PropertiesSize + 2 * 1024;
    // EVENT_TRACE_LOGFILEW (x64) is 448 bytes; the offsets of the fields we set.
    private const int LogfileSize = 448, LogfileLoggerName = 8, LogfileProcessTraceMode = 28, LogfileEventRecordCallback = 424;
    private const uint WnodeFlagTracedGuid = 0x00020000, EventTraceRealTimeMode = 0x100;
    private const uint ProcessTraceModeRealTime = 0x100, ProcessTraceModeEventRecord = 0x10000000;
    private const uint ControlStop = 1, EnableProvider = 1, ErrorAlreadyExists = 183;
    private const ulong InvalidProcessTraceHandle = ulong.MaxValue;

    private FpsTracker? _tracker;
    private ulong _session, _trace = InvalidProcessTraceHandle;
    private IntPtr _logfile, _loggerName;
    private Thread? _thread;
    private EventRecordCallback? _callback; // kept alive: native code calls it
    /// <summary>Sessions whose reader thread didn't stop in time: kept (with their callback and memory) so a late
    /// event can never call into freed memory or a collected delegate.</summary>
    private static readonly List<EtwPresentSession> Lingering = new();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr record);

    /// <returns>Null when running, otherwise why FPS is unavailable.</returns>
    public string? Start(FpsTracker tracker)
    {
        if (_thread is not null) return null;
        _tracker = tracker;
        StopSession(); // a stale session from a crash would make StartTrace fail
        var props = NewProperties();
        try
        {
            var err = StartTraceW(out _session, SessionName, props);
            if (err == ErrorAlreadyExists) { StopSession(); err = StartTraceW(out _session, SessionName, props); }
            if (err != 0) return $"couldn't start the frame counter (error {err})";
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        foreach (var provider in new[] { Dxgi, D3d9 })
        {
            var guid = provider;
            EnableTraceEx2(_session, ref guid, EnableProvider, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
        }

        _callback = OnEvent;
        _logfile = Marshal.AllocHGlobal(LogfileSize);
        for (var i = 0; i < LogfileSize; i++) Marshal.WriteByte(_logfile, i, 0);
        _loggerName = Marshal.StringToHGlobalUni(SessionName);
        Marshal.WriteIntPtr(_logfile, LogfileLoggerName, _loggerName);
        Marshal.WriteInt32(_logfile, LogfileProcessTraceMode, unchecked((int)(ProcessTraceModeRealTime | ProcessTraceModeEventRecord)));
        Marshal.WriteIntPtr(_logfile, LogfileEventRecordCallback, Marshal.GetFunctionPointerForDelegate(_callback));
        _trace = OpenTraceW(_logfile);
        if (_trace == InvalidProcessTraceHandle)
        {
            var err = Marshal.GetLastWin32Error();
            Dispose();
            return $"couldn't read the frame counter (error {err})";
        }

        var handles = new[] { _trace };
        _thread = new Thread(() => ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "Wyvern Center FPS" };
        _thread.Start();
        return null;
    }

    private void OnEvent(IntPtr record)
    {
        // EVENT_HEADER: ProcessId at 12, TimeStamp at 16 (FILETIME), ProviderId at 24, EventDescriptor.Id at 40.
        var id = (ushort)Marshal.ReadInt16(record, 40);
        if (id != DxgiPresentStart && id != D3d9PresentStart) return;
        var provider = Marshal.PtrToStructure<Guid>(record + 24);
        if ((id == DxgiPresentStart && provider == Dxgi) || (id == D3d9PresentStart && provider == D3d9))
            _tracker?.Present(Marshal.ReadInt32(record, 12), Marshal.ReadInt64(record, 16));
    }

    private static IntPtr NewProperties()
    {
        var p = Marshal.AllocHGlobal(PropertiesBuffer);
        for (var i = 0; i < PropertiesBuffer; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, PropertiesBuffer);                       // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                                     // Wnode.ClientContext: QPC timestamps
        Marshal.WriteInt32(p, 44, unchecked((int)WnodeFlagTracedGuid));   // Wnode.Flags
        Marshal.WriteInt32(p, 48, 64);                                    // BufferSize (KB)
        Marshal.WriteInt32(p, 64, unchecked((int)EventTraceRealTimeMode)); // LogFileMode
        Marshal.WriteInt32(p, 68, 1);                                     // FlushTimer (s)
        Marshal.WriteInt32(p, 116, PropertiesSize);                       // LoggerNameOffset
        return p;
    }

    /// <summary>The session's name in earlier versions: stopped too, in case one was left running.</summary>
    private const string LegacySessionName = "MSI-Helper-FPS";

    private static void StopSession()
    {
        foreach (var name in new[] { SessionName, LegacySessionName })
        {
            var props = NewProperties();
            try { ControlTraceW(0, name, props, ControlStop); }
            finally { Marshal.FreeHGlobal(props); }
        }
    }

    public void Dispose()
    {
        StopSession(); // ends ProcessTrace
        if (_trace != InvalidProcessTraceHandle) { CloseTrace(_trace); _trace = InvalidProcessTraceHandle; }
        if (_thread is { } thread && !thread.Join(2000))
        {
            lock (Lingering) Lingering.Add(this); // never free what the reader thread may still use
            return;
        }
        _thread = null;
        if (_logfile != IntPtr.Zero) { Marshal.FreeHGlobal(_logfile); _logfile = IntPtr.Zero; }
        if (_loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerName); _loggerName = IntPtr.Zero; }
        _tracker = null;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint StartTraceW(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint ControlTraceW(ulong handle, string name, IntPtr properties, uint code);
    [DllImport("advapi32.dll")] private static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")] private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern uint CloseTrace(ulong handle);
}
