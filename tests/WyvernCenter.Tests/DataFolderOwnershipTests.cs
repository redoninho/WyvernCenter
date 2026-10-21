using System.ServiceProcess;
using WyvernCenter.Core.Features;
using WyvernCenter.Core.Safety;

namespace WyvernCenter.Tests;

/// <summary>Only the app's data-folder preparation creates the data folder: nothing else may (re)create it.</summary>
public class DataFolderOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mh-own-" + Guid.NewGuid().ToString("N"));
    private string Missing => Path.Combine(_root, "data");

    public DataFolderOwnershipTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void WriteLog_DoesNotCreateTheFolder()
    {
        new WriteLog(Path.Combine(Missing, "log.txt")).Append("hello");
        Assert.False(Directory.Exists(Missing));
    }

    [Fact]
    public void Snapshot_DoesNotCreateTheFolder()
    {
        Assert.ThrowsAny<IOException>(() => new SnapshotStore(Path.Combine(Missing, "s.json")).AddMissing(new[] { ("x", (Func<int>)(() => 1)) }));
        Assert.False(Directory.Exists(Missing));
    }

    [Fact]
    public void PowerPlanOriginals_DoNotCreateTheFolder()
    {
        var o = PowerPlanOriginals.Load(Path.Combine(Missing, "cpu-originals.json"));
        o.AddIfMissing(Guid.NewGuid(), new PlanValues(1, 1, 100, 100, 0, 0));
        Assert.ThrowsAny<IOException>(o.Save);
        Assert.False(Directory.Exists(Missing));
    }

}
