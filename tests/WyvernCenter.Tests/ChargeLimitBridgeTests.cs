using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Safety;
using WyvernCenter.Core.Scm;

namespace WyvernCenter.Tests;

/// <summary>Battery charge limit (EC 0xEF) through MSI's Dragon Center service on WMI v1 laptops.</summary>
public class ChargeLimitBridgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mh-cl-" + Guid.NewGuid().ToString("N"));
    private readonly FakeScm _scm = new();
    private readonly FakeBridge _bridge = new();

    public ChargeLimitBridgeTests()
    {
        Directory.CreateDirectory(_dir);
        _scm[ScmClass.System, 7] = 0xC1;
        _scm[ScmClass.System, 9] = 0x8D;
        _bridge.Ec[0xEF] = 0xE4; // 100 %, bit 7 set (as read on the GF63)
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private EcController Gf63(FakeBridge? bridge) =>
        new(ProfileCatalog.Match("16R5EMS1.102"), new EcAccess(_scm, null, bridge),
            new SnapshotStore(Path.Combine(_dir, "s.json")), new WriteLog(Path.Combine(_dir, "l.txt")), canWrite: true);

    [Fact]
    public void Gf63_SetsTheLimitAndKeepsBit7()
    {
        var ec = Gf63(_bridge);
        Assert.True(ec.IsAvailable("chargeLimit"));
        Assert.True(ec.Set("chargeLimit", 80).Ok);
        Assert.Equal(0xD0, _bridge.Ec[0xEF]);
        Assert.Equal(80, ec.Get("chargeLimit"));
        Assert.True(ec.Set("chargeLimit", 60).Ok);
        Assert.Equal(0xBC, _bridge.Ec[0xEF]);
    }

    [Fact]
    public void SwitchedOff_IsUnavailableAndNeverTouched()
    {
        var off = new FakeBridge { Available = false };
        var ec = Gf63(off);
        Assert.False(ec.IsAvailable("chargeLimit"));
        Assert.False(ec.Set("chargeLimit", 80).Ok);
        Assert.Empty(off.Writes);
        Assert.False(Gf63(null).IsAvailable("chargeLimit")); // no bridge at all
    }

    [Fact]
    public void OutsideRange_Rejected()
    {
        var ec = Gf63(_bridge);
        Assert.False(ec.Set("chargeLimit", 40).Ok);
        Assert.False(ec.Set("chargeLimit", 101).Ok);
        Assert.Empty(_bridge.Writes);
    }

    [Fact]
    public void FirmwareWrites_DoNotStartMsisService()
    {
        Assert.True(Gf63(_bridge).Set("shiftMode", 0xC2).Ok);
        Assert.Equal(0, _bridge.Reads); // the snapshot reads bridge settings only before their own first write
    }

    [Fact]
    public void OriginalLimit_IsSnapshottedAndRestored()
    {
        var ec = Gf63(_bridge);
        Assert.True(ec.Set("chargeLimit", 60).Ok);
        Assert.All(ec.RestoreSnapshot(), r => Assert.True(r.Ok, r.Setting + ": " + r.Message));
        Assert.Equal(0xE4, _bridge.Ec[0xEF]);
    }

    [Fact]
    public void Generic_RefusesWhenBit7IsClear()
    {
        _bridge.Ec[0xEF] = 0x50;                         // not MSI's charge-limit encoding on this model
        var ec = new EcController(ProfileCatalog.Generic, new EcAccess(_scm, null, _bridge),
            new SnapshotStore(Path.Combine(_dir, "g.json")), new WriteLog(Path.Combine(_dir, "g.txt")), canWrite: true);
        Assert.False(ec.Set("chargeLimit", 80).Ok);
        Assert.Equal(0x50, _bridge.Ec[0xEF]);
    }

    [Theory]
    [InlineData("16R5EMS1.102")]
    [InlineData("16V1EMS1.111")]
    public void Wmi1Profiles_HaveTheBridgeChargeLimit(string firmware)
    {
        var s = ProfileCatalog.Match(firmware)!.Settings["chargeLimit"];
        Assert.Equal(Route.Bridge, s.Route);
        Assert.Equal(0xEF, s.EcOffset);
        Assert.Equal(0x7F, s.Mask);
        Assert.True(s.Verified);
    }
}
