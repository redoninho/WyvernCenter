using WyvernCenter.Core.Profile;

namespace WyvernCenter.Core.Safety;

public enum SetOutcome { Ok, Rejected, Failed }

public sealed record SetResult(string Setting, SetOutcome Outcome, string Message)
{
    public bool Ok => Outcome == SetOutcome.Ok;
}

/// <summary>
/// The only component allowed to write hardware. Every write is: known setting, writable, verified on this
/// firmware, not failed this session, route available, value whitelisted; then snapshot, masked
/// read-modify-write, read-back check, revert on mismatch, log.
/// </summary>
public sealed class EcController
{
    private readonly ModelProfile? _profile;
    private readonly IEcAccess _access;
    private readonly SnapshotStore _snapshots;
    private readonly WriteLog _log;
    private readonly bool _canWrite;
    private readonly HashSet<string> _failed = new();
    // One lock per route (SCM0 and WMI v2 are separate firmware interfaces).
    private readonly object _scmLock = new();
    private readonly object _wmi2Lock = new();
    // The snapshot file and _snapshotted. Never held while reading hardware, so a slow read can't hold up another
    // route's writes (e.g. the thermal failsafe's).
    private readonly object _snapshotLock = new();
    private readonly object _stateLock = new();     // guards _failed and the read-error log set
    private readonly HashSet<string> _snapshotted = new();
    private bool _snapshotCorruptLogged;

    private readonly bool _canTest;
    private volatile bool _testing;

    /// <param name="canTest">Admin rights and a safe data folder: the "Verify my laptop" wizard may run.</param>
    public EcController(ModelProfile? profile, IEcAccess access, SnapshotStore snapshots, WriteLog log, bool canWrite, bool canTest = false)
    {
        _canTest = canTest;
        _profile = profile;
        _access = access;
        _snapshots = snapshots;
        _log = log;
        _canWrite = canWrite;
    }

    public ModelProfile? Profile => _profile;

    /// <summary>True when no profile matched this firmware or the process lacks admin rights.</summary>
    public bool ReadOnlyMode => _profile is null || !_canWrite;

    public bool Has(string name) => _profile?.Settings.ContainsKey(name) ?? false;

    /// <summary>Whether a setting can be written right now.</summary>
    public bool IsAvailable(string name)
    {
        if (ReadOnlyMode || !_profile!.Settings.TryGetValue(name, out var s)) return false;
        if (s.ReadOnly || !s.Verified) return false;
        lock (_stateLock)
            if (_failed.Contains(name)) return false;
        return _access.IsAvailable(s); // must stay cheap: the UI thread calls this
    }

    private object LockFor(SettingDef s) => s.Route == Route.Wmi2 ? _wmi2Lock : _scmLock;

    /// <summary>Whether a setting can be read right now.</summary>
    public bool IsReadable(string name) =>
        _profile is not null && _profile.Settings.TryGetValue(name, out var s) && _access.IsAvailable(s);

    public int Get(string name)
    {
        var s = Def(name);
        lock (LockFor(s))
            return (_access.ReadRaw(s) & s.Mask) >> s.Shift;
    }

    public int? TryGet(string name)
    {
        try { return IsReadable(name) ? Get(name) : null; }
        catch (Exception ex)
        {
            lock (_stateLock)
                if (_readErrorsLogged.Add(name)) _log.Append($"{name}: read failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private readonly HashSet<string> _readErrorsLogged = new();

    public SetResult Set(string name, int value) => SetCore(name, value, restoring: false);

    // ---- "Verify my laptop" test session ----------------------------------------------------------

    /// <summary>The wizard is running: <see cref="TestSet"/> may change testable settings.</summary>
    public bool Testing => _testing;

    public bool BeginTest()
    {
        if (!_canTest || _profile is null) return false;
        _testing = true;
        _log.Append("verify: test session started");
        return true;
    }

    public void EndTest()
    {
        if (!_testing) return;
        _testing = false;
        _log.Append("verify: test session ended");
    }

    /// <summary>Whether the wizard can change this setting now.</summary>
    public bool CanTest(string name)
    {
        if (!_testing || _profile is null || !_profile.Settings.TryGetValue(name, out var s) || !s.Testable) return false;
        lock (_stateLock)
            if (_failed.Contains(name)) return false;
        return _access.IsAvailable(s);
    }

    /// <summary>
    /// A wizard test write: only while testing, only testable settings, still whitelisted, layout-checked, snapshotted
    /// and read back (reverted on mismatch). <paramref name="restoring"/> puts the value from before the test back.
    /// </summary>
    public SetResult TestSet(string name, int value, bool restoring = false) => SetCore(name, value, restoring, test: true);

    /// <param name="restoring">Putting a snapshot original back: it may be a value outside the writable set (e.g.
    /// a mode Wyvern Center doesn't offer), and the layout check doesn't apply to going back.</param>
    private SetResult SetCore(string name, int value, bool restoring, bool test = false)
    {
        if (test)
        {
            if (!_testing) return Reject(name, "no test session");
            if (_profile is null || !_profile.Settings.TryGetValue(name, out var t) || !t.Testable) return Reject(name, "not testable");
        }
        else if (ReadOnlyMode) return Reject(name, "read-only mode");
        if (!_profile!.Settings.TryGetValue(name, out var s)) return Reject(name, "unknown setting");
        if (!test && s.ReadOnly) return Reject(name, "read-only setting");
        if (!test && !s.Verified) return Reject(name, "not verified on this firmware");
        if (!restoring && !s.Allows(value)) return Reject(name, $"value {value} not allowed");

        lock (LockFor(s))
        {
            if (IsFailed(name)) return Reject(name, "failed earlier this session");
            if (!_access.IsAvailable(s)) return Reject(name, "route unavailable");

            // Fail closed: without its original recorded, a write could never be undone by Restore. Not marked as
            // failed, so the next attempt (e.g. after a transient read error) tries again.
            if (!EnsureSnapshot(s)) return Done(name, SetOutcome.Rejected, "original value couldn't be read; not written");

            int? old = null;
            try
            {
                old = _access.ReadRaw(s);
                if (!restoring && !s.LooksRight(old.Value))
                    return Done(name, SetOutcome.Rejected, $"{Hex(old.Value)} doesn't match the expected layout for this model; not written");
                var wanted = (old.Value & ~s.Mask) | ((value << s.Shift) & s.Mask);
                if (wanted == old.Value) return Done(name, SetOutcome.Ok, $"{Hex(old.Value)} unchanged");

                _access.WriteRaw(s, wanted);
                var back = _access.ReadRaw(s);
                if ((back & s.Mask) != (wanted & s.Mask))
                {
                    TryRestore(s, old.Value);
                    MarkFailed(name);
                    return Done(name, SetOutcome.Failed, $"{Hex(old.Value)} -> {Hex(wanted)} read back {Hex(back)}; reverted");
                }
                return Done(name, SetOutcome.Ok, $"{Hex(old.Value)} -> {Hex(wanted)}");
            }
            catch (Exception ex)
            {
                if (old is not null) TryRestore(s, old.Value);
                MarkFailed(name);
                return Done(name, SetOutcome.Failed, $"error: {ex.Message}");
            }
        }
    }

    /// <summary>False when no snapshot exists yet or the file is unreadable (then Restore must not guess).</summary>
    public bool CanRestore
    {
        get
        {
            lock (_snapshotLock) return _snapshots.Exists && _snapshots.TryLoad(out _);
        }
    }

    /// <summary>Puts every verified setting back to its pre-Wyvern Center value. Modes go last, after the curves they use.</summary>
    /// <param name="include">Only these settings (e.g. just the fans, or everything but the fans); null = all.</param>
    public IReadOnlyList<SetResult> RestoreSnapshot(Func<string, bool>? include = null)
    {
        if (ReadOnlyMode) return new[] { Reject("*", "read-only mode") };
        IReadOnlyDictionary<string, int> snapshot;
        lock (_snapshotLock)
            if (!_snapshots.TryLoad(out snapshot)) return new[] { Reject("*", "snapshot file unreadable; not guessing") };
        var results = new List<SetResult>();
        foreach (var (name, raw) in snapshot.OrderBy(kv => kv.Key is "fanMode" or "shiftMode" ? 1 : 0))
        {
            if (include is not null && !include(name)) continue;
            if (!_profile!.Settings.TryGetValue(name, out var s) || s.ReadOnly || !s.Verified) continue;
            results.Add(SetCore(name, (raw & s.Mask) >> s.Shift, restoring: true));
        }
        _log.Append($"restore snapshot: {results.Count(r => r.Ok)}/{results.Count} ok");
        return results;
    }

    /// <summary>
    /// Records originals before the first write: every writable setting not yet in the snapshot.
    /// Values are read without holding the snapshot lock; entries are add-only, so an extra read can't replace an
    /// original recorded earlier.
    /// </summary>
    /// <returns>False when the target's original couldn't be recorded (the caller must not write).</returns>
    private bool EnsureSnapshot(SettingDef target)
    {
        List<SettingDef> due;
        lock (_snapshotLock)
            due = _profile!.Settings.Values
                .Where(s => (!s.ReadOnly || s.Name == target.Name) && !_snapshotted.Contains(s.Name))
                .ToList();
        if (due.Count == 0) return true;

        var values = new List<(string Name, int Raw)>();
        foreach (var s in due)
        {
            try { values.Add((s.Name, _access.ReadRaw(s))); }
            catch
            {
                // unreadable right now: tried again before the next write
            }
        }

        lock (_snapshotLock)
        {
            try
            {
                var added = _snapshots.AddMissing(values.Select(v => (v.Name, (Func<int>)(() => v.Raw))), out var recorded);
                if (added < 0)
                {
                    if (!_snapshotCorruptLogged) _log.Append("snapshot file unreadable: left untouched, Restore disabled");
                    _snapshotCorruptLogged = true;
                    return true; // Restore is off anyway; don't block the hardware (and never the failsafe) over it
                }
                foreach (var name in recorded) _snapshotted.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Append("snapshot file couldn't be written: " + ex.Message);
            }
            return _snapshotted.Contains(target.Name);
        }
    }

    private bool IsFailed(string name)
    {
        lock (_stateLock) return _failed.Contains(name);
    }

    private void MarkFailed(string name)
    {
        lock (_stateLock) _failed.Add(name);
    }

    private void TryRestore(SettingDef s, int raw)
    {
        try { _access.WriteRaw(s, raw); }
        catch (Exception ex) { _log.Append($"{s.Name}: restore of {Hex(raw)} failed: {ex.Message}"); }
    }

    private SettingDef Def(string name) =>
        _profile is not null && _profile.Settings.TryGetValue(name, out var s)
            ? s
            : throw new KeyNotFoundException($"No setting '{name}' in the active profile.");

    private SetResult Reject(string name, string why) => Done(name, SetOutcome.Rejected, why);

    private SetResult Done(string name, SetOutcome outcome, string message)
    {
        _log.Append($"{name}: {outcome} {message}");
        return new SetResult(name, outcome, message);
    }

    private static string Hex(int v) => $"0x{v:X2}";
}
