using System.Text;
using WyvernCenter.Core.AcpiWmi;
using WyvernCenter.Core.Monitoring;
using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Safety;
using WyvernCenter.Core.Scm;
using WyvernCenter.Core.Wmi2;

namespace WyvernCenter.Tests;

/// <summary>In-memory MSI_ACPI firmware: EC RAM behind Get_Data/Set_Data plus the version and fan blocks.</summary>
public sealed class FakeWmi2 : IWmi2Transport
{
    public byte[] Ram { get; } = new byte[256];
    public int Major { get; set; } = 2;
    public string Firmware { get; set; } = "1585EMS1.11202232023" + "12:34:56";
    public byte EcFlags { get; set; } = 0x91; // TigerLake-class, layout 1.1
    public byte[] Tach { get; set; } = { 0x00, 0xC8, 0x01, 0x90 };
    public List<(Wmi2Method Method, byte[] Input)> Calls { get; } = new();

    public byte[] Call(Wmi2Method method, byte[] input)
    {
        Assert.Equal(Wmi2Protocol.PackageSize, input.Length);
        Calls.Add((method, input));
        var output = new byte[32];
        output[0] = 1;
        switch (method)
        {
            case Wmi2Method.GetWmi:
                output[1] = (byte)Major;
                output[2] = 0;
                break;
            case Wmi2Method.GetEc:
                output[1] = EcFlags;
                Encoding.ASCII.GetBytes(Firmware).CopyTo(output, 2);
                break;
            case Wmi2Method.GetData:
                output[1] = Ram[input[0]];
                break;
            case Wmi2Method.SetData:
                Ram[input[0]] = input[1];
                break;
            case Wmi2Method.GetFan when input[0] == 0:
                Tach.CopyTo(output, 1);
                break;
            default:
                throw new AcpiWmiException(AcpiWmiException.ItemNotFound, method.ToString());
        }
        return output;
    }
}

public class Wmi2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wc-wmi2-" + Guid.NewGuid().ToString("N"));

    public Wmi2Tests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Input_PutsTheParameterFirstAndDataAfterIt()
    {
        var input = Wmi2Protocol.Input(0xD2, new byte[] { 0xC4 });
        Assert.Equal(32, input.Length);
        Assert.Equal(0xD2, input[0]);
        Assert.Equal(0xC4, input[1]);
        Assert.All(input.Skip(2), b => Assert.Equal(0, b));
        Assert.Throws<ArgumentException>(() => Wmi2Protocol.Input(0, new byte[32]));
    }

    [Fact]
    public void Reply_SplitsTheStatusByteFromThePayload()
    {
        var r = Wmi2Protocol.Parse(new byte[] { 1, 0xAB, 0xCD });
        Assert.True(r.Ok);
        Assert.Equal(new byte[] { 0xAB, 0xCD }, r.Data);
        Assert.False(Wmi2Protocol.Parse(new byte[] { 0, 0xAB }).Ok);
        Assert.False(Wmi2Protocol.Parse(Array.Empty<byte>()).Ok);
    }

    [Fact]
    public void Version_BelowTwoMeansNoWmi2()
    {
        Assert.Equal((2, 1), Wmi2Protocol.Version(new byte[] { 1, 2, 1 }));
        Assert.Null(Wmi2Protocol.Version(new byte[] { 1, 1, 0 }));
        Assert.Null(Wmi2Protocol.Version(new byte[] { 1 }));
    }

    [Fact]
    public void EcInfo_DecodesTheFlagsAndFirmware()
    {
        var output = new byte[32];
        output[0] = 1;
        output[1] = 0xD2; // TigerLake-class, page changed, layout 1.2
        Encoding.ASCII.GetBytes("1585EMS1.1120223202312:34:56").CopyTo(output, 2);
        var ec = Wmi2Protocol.EcInfo(output)!;
        Assert.True(ec.TigerLakeClass);
        Assert.True(ec.EcRamPageChanged);
        Assert.Equal(1, ec.EcRamMainVersion);
        Assert.Equal(2, ec.EcRamSubVersion);
        Assert.Equal("1585EMS1.112 02232023 12:34:56", ec.Firmware);
        Assert.Equal("1585EMS1.112", FirmwareId.Version(ec.Firmware));

        output[0] = 0;
        Assert.Null(Wmi2Protocol.EcInfo(output));
        Assert.Null(Wmi2Protocol.EcInfo(new byte[10]));
    }

    [Fact]
    public void Detect_ReadsVersionAndFirmware()
    {
        var fw = new FakeWmi2();
        var ec = Wmi2Ec.Detect(fw)!;
        Assert.Equal(2, ec.Major);
        Assert.Equal("1585EMS1.112 02232023 12:34:56", ec.Firmware);
        Assert.All(fw.Calls, c => Assert.True(c.Method is Wmi2Method.GetWmi or Wmi2Method.GetEc)); // detection only reads
    }

    [Fact]
    public void Detect_RefusesWmiVersionOne()
    {
        Assert.Null(Wmi2Ec.Detect(new FakeWmi2 { Major = 1 }));
    }

    [Fact]
    public void ReadByte_UsesGetDataAtTheOffset()
    {
        var fw = new FakeWmi2();
        fw.Ram[0xD2] = 0xC4;
        Assert.Equal(0xC4, Wmi2Ec.Detect(fw)!.ReadByte(0xD2));
        Assert.Equal((Wmi2Method.GetData, (byte)0xD2), (fw.Calls[^1].Method, fw.Calls[^1].Input[0]));
    }

    [Fact]
    public void Writes_AreOffInTheReadOnlyBeta()
    {
        var fw = new FakeWmi2();
        var ec = Wmi2Ec.Detect(fw)!;
        Assert.False(ec.WritesEnabled);
        Assert.Throws<NotSupportedException>(() => ec.WriteByte(0xD2, 0xC1));
        Assert.DoesNotContain(fw.Calls, c => c.Method == Wmi2Method.SetData);
    }

    [Fact]
    public void ReadBlock_OnlyAcceptsGetMethods()
    {
        var ec = Wmi2Ec.Detect(new FakeWmi2())!;
        Assert.Equal(new byte[] { 0x00, 0xC8, 0x01, 0x90 }, ec.ReadBlock(Wmi2Method.GetFan, 0)[..4]);
        Assert.Throws<ArgumentException>(() => ec.ReadBlock(Wmi2Method.SetFan, 1));
    }

    [Fact]
    public void AnyWmi2Firmware_GetsTheReadOnlyWmi2Profile()
    {
        var m = ProfileCatalog.Resolve("1585EMS1.112", FirmwareInterface.Wmi2);
        Assert.Equal(ModelSupport.Generic, m.Support);
        var p = m.Profile!;
        Assert.Equal(FirmwareInterface.Wmi2, p.Interface);
        Assert.All(p.Settings.Values, s => Assert.True(s.ReadOnly, s.Name));
        Assert.All(p.Settings.Values, s => Assert.Equal(Route.Wmi2, s.Route));
        // The fixed WMI v2 addresses MSI Center uses (and msi-ec's G2 layouts).
        Assert.Equal(0xD2, p.Settings["shiftMode"].EcOffset);
        Assert.Equal(0xD4, p.Settings["fanMode"].EcOffset);
        Assert.Equal(0xD7, p.Settings["chargeLimit"].EcOffset);
        Assert.Equal(0x98, p.Settings["coolerBoost"].EcOffset);
        Assert.Equal(new Wmi2Block(Wmi2Method.GetFan, 0, 1), p.Settings["cpuFanTachLo"].Block);
    }

    [Fact]
    public void ProfilesOnlyMatchTheirOwnInterface()
    {
        Assert.Equal(ModelSupport.Supported, ProfileCatalog.Resolve("16R5EMS1.102").Support);
        var wmi2 = ProfileCatalog.Resolve("16R5EMS1.102", FirmwareInterface.Wmi2);
        Assert.Equal(ModelSupport.Generic, wmi2.Support);
        Assert.Equal(FirmwareInterface.Wmi2, wmi2.Profile!.Interface);
        Assert.Equal(FirmwareInterface.Scm, ProfileCatalog.Generic.Interface);
    }

    [Fact]
    public void ProfileParse_RejectsAWritableBlockSetting()
    {
        var json = """
            { "name": "x", "firmware": [], "msiServices": [], "interface": "wmi2",
              "curve": { "minSpeed": 0, "maxSpeed": 150, "floorAtIndex": 5, "floorSpeed": 100 },
              "settings": { "t": { "route": "wmi2", "block": { "method": "GetFan", "parameter": 0, "byte": 0 } } } }
            """;
        Assert.Throws<InvalidDataException>(() => ProfileCatalog.Parse(System.Text.Json.JsonDocument.Parse(json).RootElement));
    }

    private (EcController Ec, FakeWmi2 Fw) Controller(bool canWrite = true)
    {
        var fw = new FakeWmi2();
        var ec = new EcController(ProfileCatalog.GenericFor(FirmwareInterface.Wmi2),
            new EcAccess(new FakeScm(), Wmi2Ec.Detect(fw)),
            new SnapshotStore(Path.Combine(_dir, "s.json")), new WriteLog(Path.Combine(_dir, "l.txt")), canWrite);
        return (ec, fw);
    }

    [Fact]
    public void Controller_ReadsSettingsAndSensorsOverWmi2()
    {
        var (ec, fw) = Controller();
        fw.Ram[0xD2] = 0xC2;
        fw.Ram[0xD7] = 0x80 | 80;
        fw.Ram[0x98] = 0x82;
        fw.Ram[0x68] = 61;
        Assert.Equal(0xC2, ec.Get("shiftMode"));
        Assert.Equal(80, ec.Get("chargeLimit"));
        Assert.Equal(1, ec.Get("coolerBoost"));
        Assert.Equal(61, ec.TryGet("cpuTemp"));
        Assert.Equal(480000 / 0x00C8, FanRpm.FromTach(ec.TryGet("cpuFanTachLo"), ec.TryGet("cpuFanTachHi")));
    }

    [Fact]
    public void Controller_NeverWritesWmi2()
    {
        var (ec, fw) = Controller(canWrite: true);
        fw.Ram[0xD2] = 0xC1;
        Assert.False(ec.IsAvailable("shiftMode"));
        Assert.False(ec.Set("shiftMode", 0xC4).Ok);
        Assert.Empty(ec.RestoreSnapshot().Where(r => r.Ok));
        Assert.DoesNotContain(fw.Calls, c => c.Method.ToString().StartsWith("Set", StringComparison.Ordinal));
        Assert.Equal(0xC1, fw.Ram[0xD2]);
    }

    [Fact]
    public void Wnode_ReturnsTheDataBlock()
    {
        var wnode = new byte[70];
        BitConverter.GetBytes(64u).CopyTo(wnode, 56);  // DataBlockOffset
        BitConverter.GetBytes(2u).CopyTo(wnode, 60);   // SizeDataBlock
        wnode[64] = 0x34;
        wnode[65] = 0x12;
        Assert.Equal(new byte[] { 0x34, 0x12 }, Wnode.SingleInstanceData(wnode));
        BitConverter.GetBytes(69u).CopyTo(wnode, 56);
        Assert.Throws<InvalidDataException>(() => Wnode.SingleInstanceData(wnode));
    }

    [Fact]
    public void GuidScm_EncodesBytesAndSignedBatteryWords()
    {
        Assert.Equal(new byte[] { 0xC4 }, GuidScmTransport.Encode(ScmClass.System, 0xC4));
        Assert.Equal(0xC4, GuidScmTransport.Decode(ScmClass.System, new byte[] { 0xC4 }));
        Assert.Equal(-2, GuidScmTransport.Decode(ScmClass.MasterBattery, GuidScmTransport.Encode(ScmClass.MasterBattery, -2)));
        Assert.Throws<OverflowException>(() => GuidScmTransport.Encode(ScmClass.System, 300));
        Assert.Throws<InvalidDataException>(() => GuidScmTransport.Decode(ScmClass.MasterBattery, new byte[] { 1 }));
    }

    [Fact]
    public void ScmGuids_AreTheFirmwaresOwn()
    {
        // From the GF63's SCM0 _WDG table (and MSI's class definitions): one GUID per block, all distinct.
        Assert.Equal(new Guid("A1753D7B-B621-DE4A-B41A-55716A0ECE7A"), ScmNames.FirmwareGuid(ScmClass.System));
        Assert.Equal(9, Enum.GetValues<ScmClass>().Select(ScmNames.FirmwareGuid).Distinct().Count());
    }

    [Fact]
    public void SupportReport_ShowsTheWmi2DumpInsteadOfAnEmptyScmSection()
    {
        var fw = new FakeWmi2();
        fw.Ram[0xD2] = 0xC4;
        var dump = Wmi2Dump.Read(Wmi2Ec.Detect(fw)!);
        Assert.Equal(0xC4, dump.EcRam[0xD2]);
        Assert.Contains(dump.Blocks, b => b.Name == "GetAp(0)" && b.Data is null);  // the fake refuses it
        Assert.DoesNotContain(fw.Calls, c => c.Method.ToString().StartsWith("Set", StringComparison.Ordinal)
                                          || c.Method is Wmi2Method.GetMasterBattery or Wmi2Method.GetSlaveBattery);

        var scm = SupportReport.ClassesToDump.ToDictionary(c => c, _ => (IReadOnlyList<int>?)null);
        var text = SupportReport.Build(new SupportReport.Info("t", "Micro-Star", "Katana", "", "", "", "", Array.Empty<string>(),
            "1585EMS1.112", ModelSupport.Generic, null, scm, Array.Empty<(string, string)>(), null, dump));
        Assert.Contains("### WMI v2 (MSI_ACPI)", text);
        Assert.Contains("D_   ", text);
        Assert.DoesNotContain("### SCM0", text);
    }
}

public class Wmi2FanModeTextTests
{
    [Theory]
    [InlineData(0x0D, "auto")]
    [InlineData(0x1D, "silent")]
    [InlineData(0x4D, "custom curve, MSI Center's encoding (bit 6)")]
    [InlineData(0x8D, "custom curve, msi-ec's encoding (bit 7)")]
    public void NamesEachEncoding(int value, string text) => Assert.Equal(text, Wmi2Dump.FanModeText(value));
}
