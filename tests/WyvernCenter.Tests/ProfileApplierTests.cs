using WyvernCenter.Core.Features;
using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Safety;
using WyvernCenter.Core.Scm;

namespace WyvernCenter.Tests;

public class ProfileApplierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mh-prof-" + Guid.NewGuid().ToString("N"));
    private readonly FakeScm _scm = new();
    private readonly FakeCpuPower _cpu = new();
    private readonly FakeOverlay _overlay = new();

    public ProfileApplierTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private EcController Ec(bool shiftVerified = true)
    {
        var p = ProfileCatalog.Match("16R5EMS1.102")!;
        var settings = p.Settings.ToDictionary(kv => kv.Key, kv => kv.Value with { Verified = kv.Key != "shiftMode" || shiftVerified });
        return new EcController(p with { Settings = settings }, new EcAccess(_scm),
            new SnapshotStore(Path.Combine(_dir, "s.json")), new WriteLog(Path.Combine(_dir, "l.txt")), true);
    }

    [Fact]
    public void Apply_WritesEverything()
    {
        var p = Presets.Create(Presets.Battery);
        var results = new ProfileApplier(Ec(), _cpu, _overlay).Apply(p);

        Assert.All(results, r => Assert.True(r.Ok, r.Setting + ": " + r.Message));
        Assert.Equal(0xC2, _scm[ScmClass.System, 7]);                                       // Super Battery
        Assert.Equal(p.CpuCurve, Enumerable.Range(11, 6).Select(i => _scm[ScmClass.Cpu, i]));
        Assert.Equal(p.GpuCurve, Enumerable.Range(11, 6).Select(i => _scm[ScmClass.Vga, i]));
        Assert.Equal(0x8D, _scm[ScmClass.System, 9]);                                       // advanced
        Assert.Equal(CpuBoost.Off, _cpu.Boost);
        Assert.Equal(2000, _cpu.MaxFrequency);
        Assert.Equal(100, _cpu.MaxState);
        Assert.Equal(PowerOverlay.Efficiency, _overlay.Current);
    }

    [Fact]
    public void Apply_CurvesBeforeAdvancedMode()
    {
        new ProfileApplier(Ec(), _cpu, _overlay).Apply(Presets.Create(Presets.Performance));
        var lastCurve = _scm.Writes.FindLastIndex(w => w.Class == ScmClass.Vga);
        var mode = _scm.Writes.FindIndex(w => w.Class == ScmClass.System && w.Index == 9);
        Assert.True(mode > lastCurve);
        Assert.Equal(0, _cpu.MaxFrequency); // no limit
    }

    [Fact]
    public void Apply_BadCurveStopsFansOnly()
    {
        var p = Presets.Create(Presets.Extreme);
        p.GpuCurve = new[] { 0, 0, 0, 0, 0, 0 };
        var results = new ProfileApplier(Ec(), _cpu, _overlay).Apply(p);
        Assert.Contains(results, r => r.Setting == "fanCurve" && !r.Ok);
        Assert.Equal(0, _scm[ScmClass.Cpu, 11]);           // nothing written to either curve
        Assert.Equal(0, _scm[ScmClass.System, 9]);         // mode untouched
        Assert.Equal(0xC4, _scm[ScmClass.System, 7]);
        Assert.Equal(CpuBoost.Aggressive, _cpu.Boost);
    }

    [Fact]
    public void Apply_SkipFans()
    {
        var results = new ProfileApplier(Ec(), _cpu, _overlay).Apply(Presets.Create(Presets.Quiet), skipFans: true);
        Assert.DoesNotContain(results, r => r.Setting is "fanMode" or "fanCurve");
        Assert.Equal(0, _scm[ScmClass.Cpu, 11]);
        Assert.Equal(CpuBoost.Off, _cpu.Boost);
    }

    [Fact]
    public void Apply_ContinuesAfterFailedStep()
    {
        _overlay.Throw = true;
        var results = new ProfileApplier(Ec(shiftVerified: false), _cpu, _overlay).Apply(Presets.Create(Presets.Quiet));
        Assert.Contains(results, r => r.Setting == "shiftMode" && !r.Ok);
        Assert.Contains(results, r => r.Setting == "powerMode" && !r.Ok);
        Assert.Equal(0x8D, _scm[ScmClass.System, 9]);
        Assert.Equal(2500, _cpu.MaxFrequency);
    }

    [Fact]
    public void Curves_FailingPartWayNeverLeaveAnInvalidCurve()
    {
        var battery = Presets.Create(Presets.Battery);
        var extreme = Presets.Create(Presets.Extreme);
        var rules = ProfileCatalog.Match("16R5EMS1.102")!.Curve;
        for (var failAfter = 0; failAfter < 24; failAfter++)
        {
            for (var i = 0; i < 6; i++)
            {
                _scm[ScmClass.Cpu, 11 + i] = battery.CpuCurve[i];
                _scm[ScmClass.Vga, 11 + i] = battery.GpuCurve[i];
            }
            _scm[ScmClass.System, 9] = 0x8D; // advanced mode is already live from the previous profile
            _scm.FailAfterWrites = failAfter;
            FanControl.ApplyCurves(Ec(), extreme.CpuCurve, extreme.GpuCurve);
            _scm.FailAfterWrites = null;
            var cpu = Enumerable.Range(11, 6).Select(i => _scm[ScmClass.Cpu, i]).ToArray();
            var gpu = Enumerable.Range(11, 6).Select(i => _scm[ScmClass.Vga, i]).ToArray();
            Assert.True(FanCurveRules.Validate(cpu, rules).Count == 0, $"fail after {failAfter}: cpu [{string.Join(",", cpu)}]");
            Assert.True(FanCurveRules.Validate(gpu, rules).Count == 0, $"fail after {failAfter}: gpu [{string.Join(",", gpu)}]");
        }
    }

    [Fact]
    public void Curves_LoweringEndsOnTheNewCurve()
    {
        var extreme = Presets.Create(Presets.Extreme);
        var battery = Presets.Create(Presets.Battery);
        new ProfileApplier(Ec(), _cpu, _overlay).Apply(extreme);
        new ProfileApplier(Ec(), _cpu, _overlay).Apply(battery);
        Assert.Equal(battery.CpuCurve, Enumerable.Range(11, 6).Select(i => _scm[ScmClass.Cpu, i]));
        Assert.Equal(battery.GpuCurve, Enumerable.Range(11, 6).Select(i => _scm[ScmClass.Vga, i]));
    }

    [Fact]
    public void Apply_OriginalsNotSaved_LeavesCpuAlone()
    {
        _cpu.ThrowOnSave = true;
        var results = new ProfileApplier(Ec(), _cpu, _overlay).Apply(Presets.Create(Presets.Battery));
        Assert.Contains(results, r => r.Setting == "cpu" && !r.Ok);
        Assert.Null(_cpu.Boost);
        Assert.Equal(-1, _cpu.MaxFrequency);
        Assert.Equal(100, _cpu.MaxState);
        Assert.Equal(0x8D, _scm[ScmClass.System, 9]); // fans and firmware still applied
    }

    [Theory]
    [InlineData(3500, true, 3500, 100)]
    [InlineData(null, true, 0, 100)]
    [InlineData(2000, false, 0, 80)]
    [InlineData(3500, false, 0, 100)]
    [InlineData(2500, false, 0, 100)]
    public void Clock_Plan(int? mhz, bool capWorks, int freq, int percent) =>
        Assert.Equal(new CpuClock.Plan(freq, percent), CpuClock.For(mhz, capWorks));

    [Fact]
    public void AutoSwitch_PicksProfileForPowerSource()
    {
        var s = new AutoSwitchSettings(true, Presets.Balanced, Presets.Battery);
        Assert.Equal(Presets.Balanced, AutoSwitch.ProfileFor(true, s));
        Assert.Equal(Presets.Battery, AutoSwitch.ProfileFor(false, s));
        Assert.Null(AutoSwitch.ProfileFor(true, s with { Enabled = false }));
    }

    [Fact]
    public void AutoSwitch_OnlyWhenThePowerSourceChanges()
    {
        var s = new AutoSwitchSettings(true, Presets.Balanced, Presets.Battery);
        Assert.Equal(Presets.Battery, AutoSwitch.OnPowerChange(previousOnAc: null, onAc: false, s));   // start-up on battery
        Assert.Null(AutoSwitch.OnPowerChange(previousOnAc: true, onAc: true, s));                     // battery % changed: no switch
        Assert.Equal(Presets.Battery, AutoSwitch.OnPowerChange(previousOnAc: true, onAc: false, s));  // unplugged
        Assert.Equal(Presets.Balanced, AutoSwitch.OnPowerChange(previousOnAc: false, onAc: true, s)); // plugged in
        Assert.Null(AutoSwitch.OnPowerChange(previousOnAc: true, onAc: false, s with { Enabled = false }));
    }
}
