using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Safety;
using WyvernCenter.Core.Verify;
using WyvernCenter.Core.Wmi2;

namespace WyvernCenter.Tests;

public class FanCurveCheckTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 21, 15, 0, 0);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wc-fancheck-" + Guid.NewGuid().ToString("N"));

    public FanCurveCheckTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private (EcController Ec, FakeWmi2 Fw, VerifySession Session) Wmi2Laptop()
    {
        var fw = new FakeWmi2();
        fw.Ram[0xD4] = 0x0D; // fan mode auto
        var wmi2 = Wmi2Ec.Detect(fw)!;
        wmi2.WritesEnabled = true;
        var ec = new EcController(ProfileCatalog.GenericFor(FirmwareInterface.Wmi2), new EcAccess(new FakeScm(), wmi2),
            new SnapshotStore(Path.Combine(_dir, "s.json")), new WriteLog(Path.Combine(_dir, "l.txt")), canWrite: false, canTest: true);
        var session = new VerifySession(ec);
        Assert.True(session.Begin());
        return (ec, fw, session);
    }

    // ---- Writes ---------------------------------------------------------------------------------

    [Fact]
    public void CurvePoints_ReadAndWriteThroughGetFanAndSetFan_KeepingTheOtherBytes()
    {
        var (ec, fw, _) = Wmi2Laptop();
        Assert.Equal(91, ec.Get("cpuCurveSpeed1"));
        Assert.Equal(58, ec.Get("gpuCurveSpeed1"));

        Assert.True(ec.TestSet("cpuCurveSpeed1", 120).Ok);
        Assert.Equal(new byte[] { 0x11, 42, 120, 111, 134, 142, 150, 0x22 }, fw.FanBlocks[1]);
        var set = fw.Calls.Last(c => c.Method == Wmi2Method.SetFan);
        Assert.Equal(1, set.Input[0]);                                   // Set_Fan(1): the CPU fan
        Assert.Equal(new byte[] { 0x11, 42, 120, 111, 134, 142, 150, 0x22 }, set.Input[1..9]);
        Assert.All(set.Input[9..], b => Assert.Equal(0, b));             // nothing past MSI's 8-byte payload
        Assert.Equal(new byte[] { 0x33, 21, 58, 106, 133, 146, 150, 0x44 }, fw.FanBlocks[2]); // GPU untouched
    }

    [Fact]
    public void FanCurveWrites_OnlyCurvePoints_OnlyWithWritesOn()
    {
        var fw = new FakeWmi2();
        var wmi2 = Wmi2Ec.Detect(fw)!;
        Assert.Throws<NotSupportedException>(() => wmi2.WriteFanCurvePoint(1, 1, 99));   // read-only beta
        wmi2.WritesEnabled = true;
        Assert.Throws<ArgumentOutOfRangeException>(() => wmi2.WriteFanCurvePoint(0, 1, 99)); // Get_Fan(0) = tachometers
        Assert.Throws<ArgumentOutOfRangeException>(() => wmi2.WriteFanCurvePoint(1, 0, 99));
        Assert.Throws<ArgumentOutOfRangeException>(() => wmi2.WriteFanCurvePoint(1, 7, 99));
        Assert.DoesNotContain(fw.Calls, c => c.Method == Wmi2Method.SetFan);
        Assert.True(new Wmi2Block(Wmi2Method.GetFan, 2, 6).Writable);
        Assert.False(new Wmi2Block(Wmi2Method.GetFan, 0, 1).Writable);
        Assert.False(new Wmi2Block(Wmi2Method.GetThermal, 1, 1).Writable);
    }

    [Fact]
    public void RefusedSetFan_IsRevertedAndReported()
    {
        var (ec, fw, _) = Wmi2Laptop();
        fw.RefuseSetFan = true;
        var r = ec.TestSet("cpuCurveSpeed0", 100);
        Assert.False(r.Ok);
        Assert.Equal(42, fw.FanBlocks[1][1]);
    }

    // ---- Session --------------------------------------------------------------------------------

    [Fact]
    public void RaiseCurves_EveryPointToTheCurvesOwnTop_ThenEverythingGoesBack()
    {
        var (_, fw, session) = Wmi2Laptop();
        var raised = session.RaiseCurves();
        Assert.True(raised.Ok, raised.Note);
        Assert.Equal(new byte[] { 0x11, 150, 150, 150, 150, 150, 150, 0x22 }, fw.FanBlocks[1]);
        Assert.Equal(new byte[] { 0x33, 150, 150, 150, 150, 150, 150, 0x44 }, fw.FanBlocks[2]);

        Assert.True(session.SetFanMode(FanCurveCheck.MsiValue).Ok);
        Assert.Equal(0x4D, fw.Ram[0xD4]);
        Assert.True(session.PutFanModeBack().Ok);
        Assert.Equal(0x0D, fw.Ram[0xD4]);
        Assert.True(session.SetFanMode(FanCurveCheck.MsiEcValue).Ok);
        Assert.Equal(0x8D, fw.Ram[0xD4]);

        var back = session.PutFansBack();
        Assert.True(back.Ok, back.Note);
        Assert.Equal(0x0D, fw.Ram[0xD4]);
        Assert.Equal(new byte[] { 0x11, 42, 91, 111, 134, 142, 150, 0x22 }, fw.FanBlocks[1]);
        Assert.Equal(new byte[] { 0x33, 21, 58, 106, 133, 146, 150, 0x44 }, fw.FanBlocks[2]);
    }

    [Fact]
    public void ClosingHalfway_PutsTheFanModeBackFirstThenTheCurves()
    {
        var (_, fw, session) = Wmi2Laptop();
        session.RaiseCurves();
        session.SetFanMode(FanCurveCheck.MsiValue);
        var callsBefore = fw.Calls.Count;
        session.End();
        Assert.Equal(0x0D, fw.Ram[0xD4]);
        Assert.Equal(42, fw.FanBlocks[1][1]);
        var writes = fw.Calls.Skip(callsBefore).Where(c => c.Method is Wmi2Method.SetData or Wmi2Method.SetFan).ToList();
        Assert.Equal(Wmi2Method.SetData, writes[0].Method); // the fan mode, before any curve point
        Assert.Equal(0xD4, writes[0].Input[0]);
    }

    [Fact]
    public void FanModeOutsideMsisLayout_IsNotWritten()
    {
        var (_, fw, session) = Wmi2Laptop();
        fw.Ram[0xD4] = 0x07; // low nibble isn't MSI's 0x0D
        Assert.False(session.SetFanMode(FanCurveCheck.MsiValue).Ok);
        Assert.Equal(0x07, fw.Ram[0xD4]);
    }

    // ---- The check ------------------------------------------------------------------------------

    private static FanCheckEvent Feed(FanCurveCheck c, ref int t, int seconds, int rpm)
    {
        var last = FanCheckEvent.None;
        for (var end = t + seconds; t <= end && last == FanCheckEvent.None; t++) last = c.Sample(rpm, T0.AddSeconds(t));
        return last;
    }

    [Fact]
    public void FirstCandidateSpeedsUpTheFans_Confirmed()
    {
        var c = new FanCurveCheck(FanCurveCheck.Candidates(0x0D), T0);
        var t = 0;
        Assert.Equal(FanCheckEvent.TryCandidate, Feed(c, ref t, FanCurveCheck.BaselineSeconds, 2400));
        Assert.Equal(FanCurveCheck.MsiValue, c.CandidateValue);              // MSI Center's way first
        Assert.Equal(FanCheckEvent.None, c.Sample(9999, T0.AddSeconds(t)));  // ignored while the write is in flight
        c.Applied(T0.AddSeconds(t), ok: true);
        Assert.Equal(FanCheckEvent.None, Feed(c, ref t, 3, 2500));
        Assert.Equal(FanCheckEvent.Finished, Feed(c, ref t, 3, 4300));
        Assert.Equal(FanCurveCheck.MsiValue, c.Confirmed);
        var (answer, summary) = c.Verdict();
        Assert.Equal(StepAnswer.Yes, answer);
        Assert.Contains("0x4D", summary);
        Assert.Contains("2400 → 4300 RPM", summary);
    }

    [Fact]
    public void NoChange_ModeGoesBack_FansSettle_SecondCandidateTried()
    {
        var c = new FanCurveCheck(FanCurveCheck.Candidates(0x0D), T0);
        var t = 0;
        Feed(c, ref t, FanCurveCheck.BaselineSeconds, 2400);
        c.Applied(T0.AddSeconds(t), true);
        Assert.Equal(FanCheckEvent.PutModeBack, Feed(c, ref t, FanCurveCheck.WatchSeconds + 1, 2450)); // not clearly faster
        c.ModeBack(T0.AddSeconds(t));
        Assert.Equal(FanCheckEvent.TryCandidate, Feed(c, ref t, FanCurveCheck.SettleSeconds + 1, 2500));
        Assert.Equal(FanCurveCheck.MsiEcValue, c.CandidateValue);
        c.Applied(T0.AddSeconds(t), true);
        Assert.Equal(FanCheckEvent.Finished, Feed(c, ref t, FanCurveCheck.WatchSeconds + 1, 2550));
        Assert.Null(c.Confirmed);
        Assert.Equal(2, c.Tried.Count);
        Assert.Equal(StepAnswer.No, c.Verdict().Answer);
    }

    [Fact]
    public void AnEncodingAlreadyInUse_IsTriedFirst()
    {
        Assert.Equal(new[] { 0x8D, 0x4D }, FanCurveCheck.Candidates(0x8D));
        Assert.Equal(new[] { 0x4D, 0x8D }, FanCurveCheck.Candidates(0x4D));
        Assert.Equal(new[] { 0x4D, 0x8D }, FanCurveCheck.Candidates(0x1D));
    }

    [Fact]
    public void RefusedWrite_MovesToTheNextCandidate_NoReadings_IsSkipped()
    {
        var c = new FanCurveCheck(FanCurveCheck.Candidates(0x0D), T0);
        var t = 0;
        Feed(c, ref t, FanCurveCheck.BaselineSeconds, 2400);
        Assert.Equal(FanCheckEvent.PutModeBack, c.Applied(T0.AddSeconds(t), ok: false));
        Assert.Null(c.Tried[0].PeakRpm);

        var none = new FanCurveCheck(FanCurveCheck.Candidates(0x0D), T0);
        var e = FanCheckEvent.None;
        for (var s = 0; s <= FanCurveCheck.BaselineSeconds && e == FanCheckEvent.None; s++) e = none.Sample(null, T0.AddSeconds(s));
        Assert.Equal(FanCheckEvent.Finished, e);
        Assert.Equal(StepAnswer.Skipped, none.Verdict().Answer);
    }

    [Theory]
    [InlineData(2400, 2700, false)] // +12 %
    [InlineData(2400, 2800, true)]  // +17 %, +400 RPM
    [InlineData(1500, 1750, false)] // +17 % but only 250 RPM
    public void Faster_NeedsBothShareAndRpm(double before, int peak, bool faster) => Assert.Equal(faster, FanCurveCheck.Faster(before, peak));
}
