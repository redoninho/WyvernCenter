using WyvernCenter.Core.Features;

namespace WyvernCenter.Tests;

public class PowerPlanOriginalsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mh-ppo-" + Guid.NewGuid().ToString("N"));
    private string PathOf => Path.Combine(_dir, "cpu-originals.json");
    private static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerf = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public PowerPlanOriginalsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void RecordsEachPlanOnceAndKeepsThem()
    {
        var o = PowerPlanOriginals.Load(PathOf);
        Assert.True(o.AddIfMissing(Balanced, new PlanValues(2, 2, 100, 100, 0, 0)));
        Assert.False(o.AddIfMissing(Balanced, new PlanValues(0, 0, 50, 50, 2000, 2000)));   // first values stay
        Assert.True(o.AddIfMissing(HighPerf, new PlanValues(2, 2, 100, 100, 0, 0)));
        o.Save();
        var back = PowerPlanOriginals.Load(PathOf);
        Assert.Equal(new[] { Balanced, HighPerf }.OrderBy(g => g), back.Plans.Keys.OrderBy(g => g));
        Assert.Equal(new PlanValues(2, 2, 100, 100, 0, 0), back.Plans[Balanced]);
    }

    [Fact]
    public void ReadsTheOldSinglePlanFile()
    {
        File.WriteAllText(PathOf, "{\"Scheme\":\"" + Balanced + "\",\"AcBoost\":2,\"DcBoost\":4,\"AcMax\":100,\"DcMax\":90,\"AcFreq\":0,\"DcFreq\":0}");
        var o = PowerPlanOriginals.Load(PathOf);
        Assert.Equal(new PlanValues(2, 4, 100, 90, 0, 0), o.Plans[Balanced]);
    }

    [Fact]
    public void ClearRemovesTheFile()
    {
        var o = PowerPlanOriginals.Load(PathOf);
        o.AddIfMissing(Balanced, new PlanValues(2, 2, 100, 100, 0, 0));
        o.Save();
        o.Clear();
        Assert.False(File.Exists(PathOf));
        Assert.Empty(PowerPlanOriginals.Load(PathOf).Plans);
    }

    [Fact]
    public void CorruptFileLoadsEmptyAndIsKeptAside()
    {
        File.WriteAllText(PathOf, "{ not json");
        var o = PowerPlanOriginals.Load(PathOf);
        Assert.Empty(o.Plans);
        Assert.True(File.Exists(PathOf + ".bad"));
    }

    [Fact]
    public void RemoveDropsOnePlan()
    {
        var o = PowerPlanOriginals.Load(PathOf);
        o.AddIfMissing(Balanced, new PlanValues(2, 2, 100, 100, 0, 0));
        o.AddIfMissing(HighPerf, new PlanValues(2, 2, 100, 100, 0, 0));
        o.Remove(Balanced);
        o.Save();
        Assert.Equal(new[] { HighPerf }, PowerPlanOriginals.Load(PathOf).Plans.Keys);
    }
}
