using WyvernCenter.Core.Features;
using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Safety;
using WyvernCenter.Core.Scm;

namespace WyvernCenter.Tests;

public class FailsafeCoolingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mh-fs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeScm _scm = new();
    private readonly EcController _ec;
    private static readonly int[] UserCurve = { 38, 92, 115, 128, 142, 150 };

    public FailsafeCoolingTests()
    {
        Directory.CreateDirectory(_dir);
        var p = ProfileCatalog.Match("16R5EMS1.102")!;
        _ec = new EcController(p, new EcAccess(_scm), new SnapshotStore(Path.Combine(_dir, "s.json")),
            new WriteLog(Path.Combine(_dir, "l.txt")), canWrite: true);
        _scm[ScmClass.System, 9] = 0x8D;
        for (var i = 0; i < 6; i++) { _scm[ScmClass.Cpu, 11 + i] = UserCurve[i]; _scm[ScmClass.Vga, 11 + i] = UserCurve[i]; }
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private int[] Speeds(ScmClass c) => Enumerable.Range(11, 6).Select(i => _scm[c, i]).ToArray();

    private void AssertMaxCurve()
    {
        Assert.All(Speeds(ScmClass.Cpu), v => Assert.Equal(150, v));
        Assert.All(Speeds(ScmClass.Vga), v => Assert.Equal(150, v));
        Assert.Equal(0x8D, _scm[ScmClass.System, 9]);
    }

    [Fact]
    public void Engage_MaxCurveWithoutEverTouchingMsiService()
    {
        var f = new FailsafeCooling(_ec);
        f.Engage();
        AssertMaxCurve();
    }

    [Fact]
    public void Enforce_ReassertsCurveOverwrittenWhileEngaged()
    {
        var f = new FailsafeCooling(_ec);
        f.Engage();
        _scm[ScmClass.System, 9] = 0x0D;
        _scm[ScmClass.Cpu, 13] = 60;
        f.Enforce();
        Assert.Equal(0x8D, _scm[ScmClass.System, 9]);
        Assert.Equal(150, _scm[ScmClass.Cpu, 13]);
    }

    [Fact]
    public void Release_PutsTheCurveBack()
    {
        var f = new FailsafeCooling(_ec);
        f.Engage();
        f.Release();
        Assert.Equal(UserCurve, Speeds(ScmClass.Cpu));
        Assert.Equal(UserCurve, Speeds(ScmClass.Vga));
        Assert.Equal(0x8D, _scm[ScmClass.System, 9]);
    }

    [Fact]
    public void Release_RestoresCurveAndFanModeFromBeforeFallback()
    {
        _scm[ScmClass.System, 9] = 0x0D;
        var f = new FailsafeCooling(_ec);
        f.Engage();
        f.Release();
        Assert.Equal(UserCurve, Speeds(ScmClass.Cpu));
        Assert.Equal(UserCurve, Speeds(ScmClass.Vga));
        Assert.Equal(0x0D, _scm[ScmClass.System, 9]);
    }

    [Fact]
    public void Release_WithUserFanChangeDuringFailsafe_DoesNotRestoreOldState()
    {
        var f = new FailsafeCooling(_ec);
        f.Engage();
        f.UserChangedFans();
        var r = f.Release();
        Assert.True(r.UserFanPending);
        Assert.All(Speeds(ScmClass.Cpu), v => Assert.Equal(150, v)); // caller applies the user's new choice
    }

    [Fact]
    public void Release_WithUnreadableBeforeState_AsksTheCallerToPutFansBack()
    {
        _scm.FailReads = (ScmClass.System, 9, 1);   // fan mode unreadable at engage
        var f = new FailsafeCooling(_ec);
        f.Engage();
        var r = f.Release();
        Assert.True(r.UserFanPending);              // the caller re-applies the profile (or the originals)
    }
}

public class CoolingHoldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wc-hold-" + Guid.NewGuid().ToString("N"));
    private readonly FakeScm _scm = new();
    private readonly CoolingHold _hold;
    private static readonly int[] UserCurve = { 38, 92, 115, 128, 142, 150 };

    public CoolingHoldTests()
    {
        Directory.CreateDirectory(_dir);
        var ec = new EcController(ProfileCatalog.Match("16R5EMS1.102")!, new EcAccess(_scm), new SnapshotStore(Path.Combine(_dir, "s.json")),
            new WriteLog(Path.Combine(_dir, "l.txt")), canWrite: true);
        _scm[ScmClass.System, 9] = 0x1D;   // silent
        for (var i = 0; i < 6; i++) { _scm[ScmClass.Cpu, 11 + i] = UserCurve[i]; _scm[ScmClass.Vga, 11 + i] = UserCurve[i]; }
        _hold = new CoolingHold(new FailsafeCooling(ec));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private int[] Speeds(ScmClass c) => Enumerable.Range(11, 6).Select(i => _scm[c, i]).ToArray();

    private void AssertMax()
    {
        Assert.All(Speeds(ScmClass.Cpu), v => Assert.Equal(150, v));
        Assert.All(Speeds(ScmClass.Vga), v => Assert.Equal(150, v));
        Assert.Equal(0x8D, _scm[ScmClass.System, 9]);
    }

    private void AssertUsers()
    {
        Assert.Equal(UserCurve, Speeds(ScmClass.Cpu));
        Assert.Equal(UserCurve, Speeds(ScmClass.Vga));
        Assert.Equal(0x1D, _scm[ScmClass.System, 9]);
    }

    [Fact]
    public void MaxCooling_FullFansThroughTheFirmwareThenBackToTheUsersFans()
    {
        Assert.True(_hold.Available);
        _hold.SetUser(true);
        Assert.True(_hold.Active);
        AssertMax();
        var (_, released) = _hold.SetUser(false);
        Assert.False(_hold.Active);
        Assert.False(released!.UserFanPending);
        AssertUsers();
    }

    [Fact]
    public void TurningMaxCoolingOff_DuringTheFailsafe_KeepsTheFansAtMax()
    {
        _hold.SetUser(true);
        _hold.SetThermal(true);
        var (_, released) = _hold.SetUser(false);
        Assert.Null(released);
        Assert.True(_hold.Active);
        AssertMax();
        _hold.SetThermal(false);
        AssertUsers();                         // the fans from before either asked
    }

    [Fact]
    public void FailsafeReleasing_WhileMaxCoolingIsOn_KeepsTheFansAtMax()
    {
        _hold.SetThermal(true);
        _hold.SetUser(true);
        Assert.Null(_hold.SetThermal(false).Released);
        AssertMax();
        _hold.SetUser(false);
        AssertUsers();
    }

    [Fact]
    public void Enforce_PutsMaxBackIfSomethingOverwroteIt()
    {
        _hold.SetUser(true);
        _scm[ScmClass.Cpu, 12] = 40;
        Assert.NotEmpty(_hold.Enforce());
        AssertMax();
        _hold.SetUser(false);
        Assert.Empty(_hold.Enforce());
    }

    [Fact]
    public void AProfileChosenWhileHeld_IsWhatComesBack()
    {
        _hold.SetUser(true);
        _hold.UserChangedFans();
        var (_, released) = _hold.SetUser(false);
        Assert.True(released!.UserFanPending);  // the caller applies the new profile's fans
    }

    [Fact]
    public void NotAvailable_WhereFanCurvesCantBeWritten()
    {
        var ec = new EcController(ProfileCatalog.Generic, new EcAccess(new FakeScm()), new SnapshotStore(Path.Combine(_dir, "g.json")),
            new WriteLog(Path.Combine(_dir, "g.txt")), canWrite: true);
        Assert.False(new CoolingHold(new FailsafeCooling(ec)).Available);
    }
}
