using WyvernCenter.Core.Features;
using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Safety;
using WyvernCenter.Core.Scm;

namespace WyvernCenter.Tests;

public class ModelSupportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mh-ms-" + Guid.NewGuid().ToString("N"));

    public ModelSupportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ExactFirmware_IsSupported()
    {
        var m = ProfileCatalog.Resolve("16R5EMS1.102");
        Assert.Equal(ModelSupport.Supported, m.Support);
        Assert.Equal("MSI GF63 Thin 10SC", m.Profile!.Name);
    }

    [Fact]
    public void SameBoardOtherFirmware_UsesThatModelButIsUnconfirmed()
    {
        var m = ProfileCatalog.Resolve("16R5EMS1.104");
        Assert.Equal(ModelSupport.SameBoard, m.Support);
        Assert.Equal("MSI GF63 Thin 10SC", m.Profile!.Name);
    }

    [Fact]
    public void UnknownMsiFirmware_GetsTheGenericProfile()
    {
        var m = ProfileCatalog.Resolve("1552EMS1.118");
        Assert.Equal(ModelSupport.Generic, m.Support);
        var p = m.Profile!;
        Assert.True(p.Generic);
        Assert.All(p.Settings.Values.Where(s => s.Name != "chargeLimit"), s => Assert.Equal(Route.Scm, s.Route)); // firmware only
        Assert.Equal(Route.Bridge, p.Settings["chargeLimit"].Route);                    // MSI's service, when switched on
        Assert.All(p.Settings.Values.Where(s => s.Name.Contains("CurveSpeed")), s => Assert.True(s.ReadOnly)); // fan scale may differ
        Assert.False(p.Settings["shiftMode"].ReadOnly);
        Assert.True(p.Settings.ContainsKey("cpuTemp"));
    }

    [Fact]
    public void NoFirmwareString_IsNotAnMsiFirmwareInterface()
    {
        var m = ProfileCatalog.Resolve("");
        Assert.Equal(ModelSupport.None, m.Support);
        Assert.Null(m.Profile);
    }

    [Fact]
    public void GenericProfile_NeverMatchesByFirmware()
    {
        Assert.Null(ProfileCatalog.Match(""));
        Assert.All(ProfileCatalog.Models, p => Assert.False(p.Generic));
    }

    private (EcController Ec, FakeScm Scm) Generic(int fanMode = 0x0D, int shift = 0xC1, int kbd = 0x80)
    {
        var scm = new FakeScm();
        scm[ScmClass.System, 9] = fanMode;
        scm[ScmClass.System, 7] = shift;
        scm[ScmClass.System, 8] = kbd;
        var ec = new EcController(ProfileCatalog.Generic, new EcAccess(scm),
            new SnapshotStore(Path.Combine(_dir, "s.json")), new WriteLog(Path.Combine(_dir, "l.txt")), canWrite: true);
        return (ec, scm);
    }

    [Fact]
    public void Failsafe_WithoutWritableCurves_MovesSilentToTheFirmwaresAutoMode()
    {
        var (ec, scm) = Generic(fanMode: 0x1D);
        var f = new FailsafeCooling(ec);
        Assert.False(f.CanMaxFans);
        f.Engage();
        Assert.Equal(0x0D, scm[ScmClass.System, 9]);   // auto: the firmware's own curve ramps with temperature
        Assert.Equal(0, scm[ScmClass.Cpu, 11]);         // curves untouched
        f.Enforce();
        var r = f.Release();
        Assert.Equal(0x1D, scm[ScmClass.System, 9]);
        Assert.False(r.UserFanPending);
    }

    [Fact]
    public void Failsafe_WithoutWritableCurves_LeavesAdvancedAlone()
    {
        var (ec, scm) = Generic(fanMode: 0x8D);          // the user's own (maybe aggressive) MSI Center curve
        var f = new FailsafeCooling(ec);
        f.Engage();
        f.Enforce();
        Assert.Equal(0x8D, scm[ScmClass.System, 9]);
        f.Release();
        Assert.Equal(0x8D, scm[ScmClass.System, 9]);
    }

    [Fact]
    public void Failsafe_OnTheGf63_StillMaxesTheCurves() =>
        Assert.True(new FailsafeCooling(new EcController(ProfileCatalog.Match("16R5EMS1.102"), new EcAccess(new FakeScm()),
            new SnapshotStore(Path.Combine(_dir, "g.json")), new WriteLog(Path.Combine(_dir, "g.txt")), canWrite: true)).CanMaxFans);

    [Fact]
    public void ProfileOnGeneric_SkipsTheFansQuietly()
    {
        var (ec, scm) = Generic(fanMode: 0x0D);
        var results = new ProfileApplier(ec, new FakeCpuPower(), new FakeOverlay()).Apply(Presets.Create(Presets.Balanced));
        Assert.DoesNotContain(results, r => !r.Ok && (r.Setting.Contains("Curve") || r.Setting is "fanMode" or "fanCurve"));
        Assert.Equal(0x0D, scm[ScmClass.System, 9]);   // never switched to advanced on an unconfirmed model
        Assert.Empty(FanControl.ApplyCurves(ec, Presets.Curve(30, 40, 55, 70, 85, 100), Presets.Curve(30, 40, 55, 70, 85, 100)));
    }

    [Fact]
    public void GenericWrite_RefusedWhenTheCurrentValueDoesntLookLikeMsisLayout()
    {
        var (ec, scm) = Generic(shift: 0x05);             // this model's System[7] isn't the shift-mode register
        Assert.False(ec.Set("shiftMode", 0xC4).Ok);
        Assert.Equal(0x05, scm[ScmClass.System, 7]);
        var (ok, scm2) = Generic(shift: 0xC1);
        Assert.True(ok.Set("shiftMode", 0xC4).Ok);
        Assert.Equal(0xC4, scm2[ScmClass.System, 7]);
    }

    [Fact]
    public void Gs66_10S_IsSupported_WithoutKeyboardOrFanCurveWrites()
    {
        var m = ProfileCatalog.Resolve("16V1EMS1.111");
        Assert.Equal(ModelSupport.Supported, m.Support);
        var p = m.Profile!;
        Assert.Equal("MSI GS66 Stealth 10S", p.Name);
        Assert.False(p.Settings.ContainsKey("kbdBacklight"));                       // per-key RGB, not EC 0xF3
        Assert.All(p.Settings.Values.Where(s => s.Name.Contains("CurveSpeed")), s => Assert.True(s.ReadOnly));
    }

    [Fact]
    public void Gs66_ShiftModeNeverSetByMsiSoftware_CanBeChangedAndRestored()
    {
        var scm = new FakeScm();
        scm[ScmClass.System, 7] = 0x80;                  // tester's 10SE with no MSI apps installed
        scm[ScmClass.System, 9] = 0x0D;
        var ec = new EcController(ProfileCatalog.Match("16V1EMS1.111"), new EcAccess(scm),
            new SnapshotStore(Path.Combine(_dir, "gs.json")), new WriteLog(Path.Combine(_dir, "gs.txt")), canWrite: true);
        Assert.True(ec.Set("shiftMode", 0xC2).Ok);
        Assert.Equal(0xC2, scm[ScmClass.System, 7]);
        Assert.All(ec.RestoreSnapshot(), r => Assert.True(r.Ok, r.Setting + ": " + r.Message));
        Assert.Equal(0x80, scm[ScmClass.System, 7]);
    }

    [Fact]
    public void Restore_PutsBackAnOriginalOutsideTheWritableValues()
    {
        var (ec, scm) = Generic(fanMode: 0x4D, shift: 0xC0); // MSI's "basic" fan mode and "sport" shift: originals only
        Assert.True(ec.Set("fanMode", 0x0D).Ok);
        Assert.True(ec.Set("shiftMode", 0xC1).Ok);
        Assert.All(ec.RestoreSnapshot(), r => Assert.True(r.Ok, r.Setting + ": " + r.Message));
        Assert.Equal(0x4D, scm[ScmClass.System, 9]);
        Assert.Equal(0xC0, scm[ScmClass.System, 7]);
    }
}
