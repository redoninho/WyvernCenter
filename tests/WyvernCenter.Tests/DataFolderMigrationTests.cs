using WyvernCenter.Core.Safety;

namespace WyvernCenter.Tests;

public class DataFolderMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mh-mig-" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_root, "M-Helper", "data");
    private string New => Path.Combine(_root, "Wyvern Center", "data");

    public DataFolderMigrationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void OnlyTheOldFolder_IsMovedWithEverythingInIt()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "snapshot.json"), "{\"shiftMode\":193}");
        Assert.Equal(DataFolderMigration.Result.Moved, DataFolderMigration.Run(Legacy, New));
        Assert.Equal("{\"shiftMode\":193}", File.ReadAllText(Path.Combine(New, "snapshot.json")));
        Assert.False(Directory.Exists(Legacy));
    }

    [Fact]
    public void BothFoldersHoldOriginals_IsBlockedAndNothingIsMergedOrOverwritten()
    {
        Directory.CreateDirectory(Legacy);
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(Legacy, "snapshot.json"), "old");
        File.WriteAllText(Path.Combine(New, "snapshot.json"), "new");
        Assert.Equal(DataFolderMigration.Result.Blocked, DataFolderMigration.Run(Legacy, New));
        Assert.Equal("new", File.ReadAllText(Path.Combine(New, "snapshot.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Legacy, "snapshot.json")));
    }

    [Fact]
    public void NoOldFolder_NothingToDo() =>
        Assert.Equal(DataFolderMigration.Result.NothingToDo, DataFolderMigration.Run(Legacy, New));

    [Fact]
    public void OldFolderInUse_IsBlockedAndNoEmptyNewFolderIsLeft()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), "{}");
        using (new FileStream(Path.Combine(Legacy, "lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(DataFolderMigration.Result.Blocked, DataFolderMigration.Run(Legacy, New));
        Assert.False(Directory.Exists(New));
        Assert.True(Directory.Exists(Legacy));
    }

    [Fact]
    public void NewFolderWithOnlyALog_IsSetAsideAndTheOldOneMovedIn()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "snapshot.json"), "orig");
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(New, "log.txt"), "stray");
        Assert.Equal(DataFolderMigration.Result.Moved, DataFolderMigration.Run(Legacy, New));
        Assert.Equal("orig", File.ReadAllText(Path.Combine(New, "snapshot.json")));
        var aside = Directory.GetDirectories(Path.GetDirectoryName(New)!, "data.stray-*").Single();
        Assert.Equal("stray", File.ReadAllText(Path.Combine(aside, "log.txt")));
    }

    [Fact]
    public void OldFolderWithoutOriginals_AndANewOne_NothingToDo()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "log.txt"), "x");
        Directory.CreateDirectory(New);
        Assert.Equal(DataFolderMigration.Result.NothingToDo, DataFolderMigration.Run(Legacy, New));
    }

    [Fact]
    public void AfterTheMove_AnOldCopyCantRecreateItsDataFolder()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), "{}");
        DataFolderMigration.Run(Legacy, New);
        Assert.True(File.Exists(Legacy));                                     // a file where the old folder was
        Assert.ThrowsAny<IOException>(() => Directory.CreateDirectory(Legacy)); // so an old build runs read-only
        Assert.Equal(DataFolderMigration.Result.NothingToDo, DataFolderMigration.Run(Legacy, New));
    }
}
