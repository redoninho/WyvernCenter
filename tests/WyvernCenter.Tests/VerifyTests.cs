using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Safety;
using WyvernCenter.Core.Verify;
using WyvernCenter.Core.Wmi2;

namespace WyvernCenter.Tests;

public class VerifyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wc-verify-" + Guid.NewGuid().ToString("N"));

    public VerifyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private (EcController Ec, FakeWmi2 Fw, Wmi2Ec Wmi2) Wmi2Laptop(bool canTest = true)
    {
        var fw = new FakeWmi2();
        fw.Ram[0xD2] = 0xC1;          // comfort
        fw.Ram[0xD3] = 0x82;          // backlight level 2
        fw.Ram[0x98] = 0x02;          // Cooler Boost off
        fw.Ram[0xD7] = 0x80 | 100;    // charge to 100 %
        var wmi2 = Wmi2Ec.Detect(fw)!;
        var ec = new EcController(ProfileCatalog.GenericFor(FirmwareInterface.Wmi2), new EcAccess(new FakeScm(), wmi2),
            new SnapshotStore(Path.Combine(_dir, "s.json")), new WriteLog(Path.Combine(_dir, "l.txt")), canWrite: false, canTest);
        return (ec, fw, wmi2);
    }

    private static VerifyStep Step(IReadOnlyList<VerifyStep> plan, string id) => plan.Single(s => s.Id == id);

    [Fact]
    public void Plan_ReadingsFirst_ThenOnlyWhatThisLaptopCanDo()
    {
        var (ec, _, _) = Wmi2Laptop();
        var plan = VerifyPlan.For(ec, maxCoolingAvailable: false);
        Assert.Equal("sensors", plan[0].Id);
        Assert.True(plan.TakeWhile(s => s.Kind != StepKind.Change).Count() > 1);
        Assert.All(plan.SkipWhile(s => s.Kind != StepKind.Change), s => Assert.Equal(StepKind.Change, s.Kind));
        Assert.Equal(new[] { "kbdChange", "modeChange", "boostChange", "chargeChange" },
            plan.Where(s => s.Kind == StepKind.Change).Select(s => s.Id));

        var gf63 = new EcController(ProfileCatalog.Match("16R5EMS1.102"), new EcAccess(new FakeScm()),
            new SnapshotStore(Path.Combine(_dir, "g.json")), new WriteLog(Path.Combine(_dir, "g.txt")), canWrite: true, canTest: true);
        var gPlan = VerifyPlan.For(gf63, maxCoolingAvailable: true);
        Assert.Contains(gPlan, s => s.Setting == VerifyPlan.MaxCooling);
        Assert.DoesNotContain(gPlan, s => s.Setting is "coolerBoost" or "chargeLimit"); // not on the GF63 any more
    }

    [Fact]
    public void ChangeStep_ChangesThenPutsBack_OnlyDuringTheSession()
    {
        var (ec, fw, wmi2) = Wmi2Laptop();
        wmi2.WritesEnabled = true;
        var plan = VerifyPlan.For(ec, false);
        var session = new VerifySession(ec);

        Assert.False(session.Apply(Step(plan, "modeChange")).Ok);   // no session yet: refused
        Assert.Equal(0xC1, fw.Ram[0xD2]);

        Assert.True(session.Begin());
        var applied = session.Apply(Step(plan, "modeChange"));
        Assert.True(applied.Ok, applied.Note);
        Assert.NotEqual(0xC1, fw.Ram[0xD2]);
        var back = session.Revert(Step(plan, "modeChange"));
        Assert.True(back.Ok, back.Note);
        Assert.Equal(0xC1, fw.Ram[0xD2]);
        session.End();
        Assert.False(ec.Testing);
    }

    [Fact]
    public void ClosingTheWizardHalfway_PutsEverythingBack()
    {
        var (ec, fw, wmi2) = Wmi2Laptop();
        wmi2.WritesEnabled = true;
        var plan = VerifyPlan.For(ec, false);
        var session = new VerifySession(ec);
        session.Begin();
        session.Apply(Step(plan, "kbdChange"));
        session.Apply(Step(plan, "boostChange"));
        session.Apply(Step(plan, "chargeChange"));
        Assert.Equal(0x82, fw.Ram[0x98]);                          // Cooler Boost on, other bits kept
        Assert.Equal(0x80 | 80, fw.Ram[0xD7]);                     // 100 % → 80 %, bit 7 kept
        session.End();
        Assert.Equal(0x82, fw.Ram[0xD3]);
        Assert.Equal(0x02, fw.Ram[0x98]);
        Assert.Equal(0x80 | 100, fw.Ram[0xD7]);
    }

    [Fact]
    public void WithoutTheWmi2WriteSwitch_NothingIsWritten()
    {
        var (ec, fw, _) = Wmi2Laptop();
        var session = new VerifySession(ec);
        session.Begin();
        Assert.False(session.Apply(Step(VerifyPlan.For(ec, false), "modeChange")).Ok);
        Assert.Equal(0xC1, fw.Ram[0xD2]);
        Assert.DoesNotContain(fw.Calls, c => c.Method == Wmi2Method.SetData);
    }

    [Fact]
    public void TheSessionNeverOpensOrdinaryWrites()
    {
        var (ec, fw, wmi2) = Wmi2Laptop();
        wmi2.WritesEnabled = true;
        ec.BeginTest();
        Assert.False(ec.Set("shiftMode", 0xC4).Ok);                // normal writes stay off on WMI v2
        Assert.False(ec.TestSet("fanMode", 0x1D).Ok);              // not marked testable
        Assert.False(ec.TestSet("shiftMode", 0x55).Ok);            // outside the allowed values
        Assert.Equal(0xC1, fw.Ram[0xD2]);
    }

    [Fact]
    public void NoSessionWithoutAdminAndADataFolder()
    {
        var (ec, _, _) = Wmi2Laptop(canTest: false);
        Assert.False(new VerifySession(ec).Begin());
        Assert.False(ec.Testing);
    }

    [Fact]
    public void Observe_CountsEachNewValue()
    {
        var (ec, fw, _) = Wmi2Laptop();
        var session = new VerifySession(ec);
        session.Track("kbdBacklight");
        fw.Ram[0xD3] = 0x83;
        session.Track("kbdBacklight");
        session.Track("kbdBacklight");
        fw.Ram[0xD3] = 0x80;
        session.Track("kbdBacklight");
        Assert.Equal(2, session.Changes("kbdBacklight"));
        Assert.Equal("seen 2 → 3 → 0 (2 changes)", session.ObservedNote("kbdBacklight"));
    }

    [Fact]
    public void TestValues_AreClearlyDifferentAndAllowed()
    {
        var shift = SettingDefView.Of(ProfileCatalog.GenericFor(FirmwareInterface.Wmi2).Settings["shiftMode"]);
        Assert.Equal(0xC4, VerifyPlan.TestValue(shift, 0xC1));      // comfort → turbo
        Assert.Equal(0xC2, VerifyPlan.TestValue(shift, 0xC4));      // turbo → eco
        var kbd = new SettingDefView("kbdBacklight", null, 0, 3);
        Assert.Equal(3, VerifyPlan.TestValue(kbd, 1));
        Assert.Equal(0, VerifyPlan.TestValue(kbd, 3));
        var charge = new SettingDefView("chargeLimit", null, 50, 100);
        Assert.Equal(80, VerifyPlan.TestValue(charge, 100));
        Assert.Equal(100, VerifyPlan.TestValue(charge, 60));
    }

    [Fact]
    public void Results_GoIntoTheSupportReport()
    {
        var md = VerifyPlan.ToMarkdown(new[] { new VerifyResult("kbdChange", "Keyboard light (test)", StepAnswer.Yes, "2 → 3 | put back 2") },
            "1585EMS1.112", new DateTime(2026, 10, 18, 12, 0, 0));
        Assert.Contains("### Verify my laptop", md);
        Assert.Contains("| Keyboard light (test) | Yes | 2 → 3 / put back 2 |", md);
    }
}
