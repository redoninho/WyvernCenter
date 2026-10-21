using System.Xml.Linq;
using WyvernCenter.Core.Safety;

namespace WyvernCenter.Tests;

public class StartupTaskXmlTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Exe = @"C:\Program Files\Wyvern Center\WyvernCenter.exe";

    private static XElement Settings(string xml) => XDocument.Parse(xml).Root!.Element(Ns + "Settings")!;

    [Fact]
    public void Build_StartsOnBatteryAndIsNeverStoppedByUnpluggingOrTimeLimit()
    {
        var s = Settings(StartupTaskXml.Build(Exe, @"PC\user", "S-1-5-21-1"));
        Assert.Equal("false", s.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", s.Element(Ns + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("PT0S", s.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("4", s.Element(Ns + "Priority")!.Value);
    }

    [Fact]
    public void Build_RunsElevatedAtThisUsersLogonWithTheTrayArgument()
    {
        var doc = XDocument.Parse(StartupTaskXml.Build(Exe, @"PC\user", "S-1-5-21-1"));
        var root = doc.Root!;
        Assert.Equal(@"PC\user", root.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!.Element(Ns + "UserId")!.Value);
        var principal = root.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal("S-1-5-21-1", principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);
        var exec = root.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.Equal($"\"{Exe}\"", exec.Element(Ns + "Command")!.Value);
        Assert.Equal("--tray", exec.Element(Ns + "Arguments")!.Value);
    }

    [Fact]
    public void Build_EscapesXmlCharactersInPaths()
    {
        var doc = XDocument.Parse(StartupTaskXml.Build(@"C:\A&B\<x>.exe", "a&b", "S-1"));
        Assert.Equal("\"C:\\A&B\\<x>.exe\"", doc.Root!.Element(Ns + "Actions")!.Element(Ns + "Exec")!.Element(Ns + "Command")!.Value);
    }

    [Fact]
    public void NeedsRepair_OldSchtasksDefaults_True_NewTask_False()
    {
        const string old = "<Settings><DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>true</StopIfGoingOnBatteries></Settings>";
        Assert.True(StartupTaskXml.NeedsRepair(old));
        Assert.False(StartupTaskXml.NeedsRepair(StartupTaskXml.Build(Exe, @"PC\user", "S-1-5-21-1")));
    }
}
