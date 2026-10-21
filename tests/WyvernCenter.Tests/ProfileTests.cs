using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Scm;

namespace WyvernCenter.Tests;

public class ProfileTests
{
    // MSI_Software[0..33] as read from the GF63 on 2026-10-14 (docs/probe/raw/scm-dump-1.txt).
    public static readonly int[] Gf63Software =
    {
        1, 3, 0, 0, 0, 0,
        49, 54, 82, 53, 69, 77, 83, 49, 46, 49, 48, 50,
        48, 51, 49, 49, 50, 48, 50, 49, 49, 55, 58, 49, 48, 58, 50, 52,
    };

    [Fact]
    public void FirmwareId_ParsesSoftwareBlock() =>
        Assert.Equal("16R5EMS1.102 03112021 17:10:24", FirmwareId.Parse(Gf63Software));

    [Fact]
    public void FirmwareId_VersionIsFirstTwelveChars() =>
        Assert.Equal("16R5EMS1.102", FirmwareId.Version(FirmwareId.Parse(Gf63Software)));

    [Fact]
    public void FirmwareId_NonPrintableBytesBecomeEmpty() =>
        Assert.Equal("", FirmwareId.Parse(new int[34]));

    [Fact]
    public void Catalog_MatchesGf63()
    {
        var p = ProfileCatalog.Match("16R5EMS1.102");
        Assert.NotNull(p);
        Assert.Equal("MSI GF63 Thin 10SC", p!.Name);
    }

    [Fact]
    public void Catalog_UnknownFirmwareReturnsNull() => Assert.Null(ProfileCatalog.Match("14C1EMS1.012"));

    [Fact]
    public void Gf63_EveryScmSettingPointsAtARealInstance()
    {
        var p = ProfileCatalog.Match("16R5EMS1.102")!;
        foreach (var s in p.Settings.Values.Where(s => s.Route == Route.Scm))
        {
            Assert.NotNull(s.Class);
            Assert.InRange(s.Index!.Value, 0, ScmNames.Count(s.Class!.Value) - 1);
        }
    }


    [Fact]
    public void Gf63_WritableSettingsDeclareAllowedValues()
    {
        var p = ProfileCatalog.Match("16R5EMS1.102")!;
        Assert.All(p.Settings.Values.Where(s => !s.ReadOnly), s =>
        {
            Assert.NotEqual(0, s.Mask);
            Assert.True(s.Values is { Count: > 0 } || (s.Min is not null && s.Max is not null), $"{s.Name} has no whitelist");
        });
    }

    [Fact]
    public void Gf63_KnownMappings()
    {
        var p = ProfileCatalog.Match("16R5EMS1.102")!;
        Assert.Equal((ScmClass.System, 7), (p.Settings["shiftMode"].Class!.Value, p.Settings["shiftMode"].Index!.Value));
        Assert.Equal((ScmClass.System, 9), (p.Settings["fanMode"].Class!.Value, p.Settings["fanMode"].Index!.Value));
        Assert.Equal(0x03, p.Settings["kbdBacklight"].Mask);
        Assert.Equal(13, p.Settings["cpuCurveSpeed2"].Index);
        Assert.Equal(6, p.Settings["cpuCurveTemp2"].Index);
        Assert.True(p.Settings["cpuTemp"].ReadOnly);
    }

    [Fact]
    public void Gf63_ShiftModesAreExactlyThoseObservedFromMsiCenter() =>
        Assert.Equal(new Dictionary<string, int> { ["comfort"] = 0xC1, ["eco"] = 0xC2, ["turbo"] = 0xC4 },
                     ProfileCatalog.Match("16R5EMS1.102")!.Settings["shiftMode"].Values);

    [Theory]
    [InlineData("shiftMode", true)]
    [InlineData("fanMode", true)]
    [InlineData("kbdBacklight", true)]
    [InlineData("cpuCurveSpeed3", true)]
    [InlineData("gpuCurveSpeed5", true)]
    public void Gf63_VerifiedOnlyWhatWasObserved(string setting, bool verified) =>
        Assert.Equal(verified, ProfileCatalog.Match("16R5EMS1.102")!.Settings[setting].Verified);

    [Fact]
    public void Gf63_CurveRules() =>
        Assert.Equal(new CurveRules(0, 150, 5, 100), ProfileCatalog.Match("16R5EMS1.102")!.Curve);
}

public class RemovedSettingsTests
{
    [Fact]
    public void WebcamWinLockAndFnWinSwap_AreGoneFromEveryProfile()
    {
        var all = ProfileCatalog.Models.Append(ProfileCatalog.Generic).Append(ProfileCatalog.GenericFor(FirmwareInterface.Wmi2));
        Assert.All(all, p => Assert.DoesNotContain(p.Settings.Keys, k => k is "webcam" or "winKeyLock" or "fnWinSwap"));
    }
}
