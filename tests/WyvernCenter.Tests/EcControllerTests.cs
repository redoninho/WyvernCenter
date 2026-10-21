using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Safety;
using WyvernCenter.Core.Scm;

namespace WyvernCenter.Tests;

public class EcControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mh-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeScm _scm = new();

    public EcControllerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ModelProfile Profile(bool verified = true)
    {
        static SettingDef Scm(string n, ScmClass c, int i, int mask, int? min = null, int? max = null,
                              Dictionary<string, int>? values = null, bool v = true, bool ro = false) =>
            new(n, Route.Scm, c, i, null, mask, values, min, max, v, ro);
        var s = new[]
        {
            Scm("shiftMode", ScmClass.System, 7, 0xFF, values: new() { ["sport"] = 0xC0, ["comfort"] = 0xC1 }, v: verified),
            Scm("winKeyLock", ScmClass.Device, 2, 0x40, 0, 1, v: verified),
            Scm("cpuCurveSpeed0", ScmClass.Cpu, 11, 0xFF, 0, 150, v: verified),
            Scm("cpuTemp", ScmClass.Cpu, 1, 0xFF, v: true, ro: true),
            Scm("draft", ScmClass.System, 3, 0xFF, 0, 10, v: false),
        };
        return new ModelProfile("Test", new[] { "X" }, Array.Empty<string>(), s.ToDictionary(x => x.Name), new CurveRules(0, 150, 5, 100));
    }

    private string SnapPath => Path.Combine(_dir, "snapshot.json");

    private EcController Controller(ModelProfile? profile = null, bool canWrite = true) =>
        new(profile ?? Profile(), new EcAccess(_scm),
            new SnapshotStore(SnapPath), new WriteLog(Path.Combine(_dir, "log.txt")), canWrite);

    [Fact]
    public void Set_UnverifiedRejectedWithoutHardwareCall()
    {
        var r = Controller().Set("draft", 5);
        Assert.Equal(SetOutcome.Rejected, r.Outcome);
        Assert.Empty(_scm.Writes);
    }

    [Fact]
    public void Set_ReadOnlySettingRejected()
    {
        Assert.Equal(SetOutcome.Rejected, Controller().Set("cpuTemp", 50).Outcome);
        Assert.Empty(_scm.Writes);
    }

    [Fact]
    public void Set_UnknownSettingRejected() => Assert.Equal(SetOutcome.Rejected, Controller().Set("nope", 1).Outcome);

    [Fact]
    public void Set_ValueOutsideWhitelistRejected()
    {
        Assert.Equal(SetOutcome.Rejected, Controller().Set("shiftMode", 0xC4).Outcome);
        Assert.Empty(_scm.Writes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(151)]
    public void Set_ValueOutsideRangeRejected(int value)
    {
        Assert.Equal(SetOutcome.Rejected, Controller().Set("cpuCurveSpeed0", value).Outcome);
        Assert.Empty(_scm.Writes);
    }

    [Fact]
    public void UnknownFirmware_AllSetsRejected()
    {
        var c = new EcController(null, new EcAccess(_scm),
            new SnapshotStore(SnapPath), new WriteLog(Path.Combine(_dir, "l.txt")), canWrite: true);
        Assert.True(c.ReadOnlyMode);
        Assert.Equal(SetOutcome.Rejected, c.Set("shiftMode", 0xC0).Outcome);
        Assert.Empty(_scm.Writes);
    }

    [Fact]
    public void NotAdmin_AllSetsRejected()
    {
        var c = Controller(canWrite: false);
        Assert.True(c.ReadOnlyMode);
        Assert.Equal(SetOutcome.Rejected, c.Set("shiftMode", 0xC0).Outcome);
        Assert.Empty(_scm.Writes);
    }

    [Fact]
    public void Set_WritesWholeByteSetting()
    {
        _scm[ScmClass.System, 7] = 0xC1;
        Assert.Equal(SetOutcome.Ok, Controller().Set("shiftMode", 0xC0).Outcome);
        Assert.Equal(0xC0, _scm[ScmClass.System, 7]);
    }

    [Fact]
    public void Set_PreservesBitsOutsideMask()
    {
        _scm[ScmClass.Device, 2] = 0x80; // touchpad bit on, Win-key lock off
        Assert.Equal(SetOutcome.Ok, Controller().Set("winKeyLock", 1).Outcome);
        Assert.Equal(0xC0, _scm[ScmClass.Device, 2]);
        Assert.Equal(SetOutcome.Ok, Controller().Set("winKeyLock", 0).Outcome);
        Assert.Equal(0x80, _scm[ScmClass.Device, 2]);
    }

    [Fact]
    public void Get_ShiftsMaskedValue()
    {
        _scm[ScmClass.Device, 2] = 0xC0;
        Assert.Equal(1, Controller().Get("winKeyLock"));
    }

    [Fact]
    public void Set_RevertsWhenReadBackDiffers()
    {
        _scm[ScmClass.System, 7] = 0xC1;
        _scm.WritesDontStick = true;
        var c = Controller();

        var r = c.Set("shiftMode", 0xC0);

        Assert.Equal(SetOutcome.Failed, r.Outcome);
        Assert.Equal((ScmClass.System, 7, 0xC1), _scm.Writes.Last()); // restore attempt with the old byte
        Assert.False(c.IsAvailable("shiftMode"));
    }

    [Fact]
    public void Set_TransportThrows_ReportsFailure()
    {
        _scm.ThrowOnWrite = true;
        var c = Controller();
        var r = c.Set("shiftMode", 0xC0);
        Assert.Equal(SetOutcome.Failed, r.Outcome);
        Assert.False(c.IsAvailable("shiftMode"));
    }

    [Fact]
    public void Set_FailedSettingRejectedForRestOfSession()
    {
        _scm.WritesDontStick = true;
        var c = Controller();
        c.Set("shiftMode", 0xC0);
        _scm.WritesDontStick = false;
        _scm.Writes.Clear();
        Assert.Equal(SetOutcome.Rejected, c.Set("shiftMode", 0xC0).Outcome);
        Assert.Empty(_scm.Writes);
    }



    [Fact]
    public void Snapshot_TakenBeforeFirstWrite()
    {
        _scm[ScmClass.System, 7] = 0xC1;
        Controller().Set("shiftMode", 0xC0);
        Assert.Equal(0xC1, new SnapshotStore(SnapPath).Load()["shiftMode"]);
    }

    [Fact]
    public void Snapshot_ExistingEntriesNeverOverwritten()
    {
        _scm[ScmClass.System, 7] = 0xC1;
        Controller().Set("shiftMode", 0xC0);
        Controller().Set("shiftMode", 0xC1); // a later session must not replace the original with 0xC0
        Assert.Equal(0xC1, new SnapshotStore(SnapPath).Load()["shiftMode"]);
    }

    [Fact]
    public void Restore_WritesSnapshotValues()
    {
        _scm[ScmClass.System, 7] = 0xC1;
        _scm[ScmClass.Device, 2] = 0x80;
        var c = Controller();
        c.Set("shiftMode", 0xC0);
        c.Set("winKeyLock", 1);

        var results = c.RestoreSnapshot();

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(SetOutcome.Ok, r.Outcome));
        Assert.Equal(0xC1, _scm[ScmClass.System, 7]);
        Assert.Equal(0x80, _scm[ScmClass.Device, 2]);
    }

    [Fact]
    public void Log_RecordsOldAndNew()
    {
        _scm[ScmClass.System, 7] = 0xC1;
        Controller().Set("shiftMode", 0xC0);
        var log = File.ReadAllText(Path.Combine(_dir, "log.txt"));
        Assert.Contains("shiftMode", log);
        Assert.Contains("0xC1 -> 0xC0", log);
    }

    [Fact]
    public void IsAvailable_DoesNotWaitForTheHardwareLock()
    {
        var c = Controller();
        _scm.BlockReads = new ManualResetEventSlim(false);
        var reader = Task.Run(() => c.Get("cpuTemp")); // holds the EC lock while "hardware" is slow
        Assert.True(_scm.ReadStarted.Wait(2000));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var available = c.IsAvailable("shiftMode");
        sw.Stop();

        _scm.BlockReads.Set();
        reader.Wait();
        Assert.True(available);
        Assert.True(sw.ElapsedMilliseconds < 200, $"IsAvailable blocked for {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Snapshot_CorruptFileDoesNotBlockWrites()
    {
        File.WriteAllText(SnapPath, "{ not json");
        _scm[ScmClass.System, 7] = 0xC1;
        var c = Controller();
        Assert.Equal(SetOutcome.Ok, c.Set("shiftMode", 0xC0).Outcome);
        Assert.Equal("{ not json", File.ReadAllText(SnapPath)); // kept for inspection, never overwritten
        Assert.False(c.CanRestore);
    }

    [Fact]
    public void Restore_CorruptSnapshotIsRejected()
    {
        File.WriteAllText(SnapPath, "{ not json");
        var results = Controller().RestoreSnapshot();
        Assert.All(results, r => Assert.Equal(SetOutcome.Rejected, r.Outcome));
        Assert.Empty(_scm.Writes);
    }



    [Fact]
    public void Snapshot_TakenOncePerSession()
    {
        var c = Controller();
        c.Set("shiftMode", 0xC0);
        var reads = _scm.Reads;
        c.Set("shiftMode", 0xC1);
        Assert.Equal(2, _scm.Reads - reads); // read old + read back; no snapshot work
    }


    [Fact]
    public void SnapshotRetriesASettingWhoseReadFailedTheFirstTime()
    {
        var scm = new FakeScm();
        var dir = Path.Combine(Path.GetTempPath(), "mh-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var profile = WyvernCenter.Core.Profile.ProfileCatalog.Match("16R5EMS1.102")!;
            var kbd = profile.Settings["kbdBacklight"];
            scm[kbd.Class!.Value, kbd.Index!.Value] = 0x83;
            var store = new SnapshotStore(Path.Combine(dir, "s.json"));
            var ec = new EcController(profile, new EcAccess(scm), store, new WriteLog(Path.Combine(dir, "l.txt")), canWrite: true);
            scm.FailReads = (kbd.Class!.Value, kbd.Index!.Value, 1);        // transient error during the first snapshot
            ec.Set("shiftMode", profile.Settings["shiftMode"].Values!["comfort"]);
            ec.Set("kbdBacklight", 1);
            Assert.True(store.TryLoad(out var snap));
            Assert.Equal(0x83, snap["kbdBacklight"]);                        // the real original, recorded before the write
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }


    [Fact]
    public void UnknownOriginalMeansNoWrite_ThenTheRealOriginalIsRecorded()
    {
        var scm = new FakeScm();
        var dir = Path.Combine(Path.GetTempPath(), "mh-snap2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var profile = WyvernCenter.Core.Profile.ProfileCatalog.Match("16R5EMS1.102")!;
            var kbd = profile.Settings["kbdBacklight"];
            scm[kbd.Class!.Value, kbd.Index!.Value] = 0x83;
            var store = new SnapshotStore(Path.Combine(dir, "s.json"));
            var ec = new EcController(profile, new EcAccess(scm), store, new WriteLog(Path.Combine(dir, "l.txt")), canWrite: true);
            scm.FailReads = (kbd.Class!.Value, kbd.Index!.Value, 1);   // its own snapshot read fails once
            var first = ec.Set("kbdBacklight", 1);
            Assert.False(first.Ok);                                        // original unknown: refuse rather than lose it
            Assert.Equal(0x83, scm[kbd.Class!.Value, kbd.Index!.Value]);
            Assert.True(ec.Set("kbdBacklight", 2).Ok);                     // not marked failed: the next try works
            Assert.True(store.TryLoad(out var snap));
            Assert.Equal(0x83, snap["kbdBacklight"]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
