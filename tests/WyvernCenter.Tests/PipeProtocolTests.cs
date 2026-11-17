using System.Text;
using WyvernCenter.Core.Bridge;

namespace WyvernCenter.Tests;

public class PipeProtocolTests
{
    [Fact]
    public void Frame_HeaderIsZeroFfThenBigEndianTotalLength()
    {
        var f = PipeProtocol.Frame("HS:REQ:M-Helper;1.0");
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x00, 0x00, 0x00, (byte)(19 + 6) }, f[..6]);
        Assert.Equal("HS:REQ:M-Helper;1.0", Encoding.ASCII.GetString(f[6..]));
    }

    [Fact]
    public void ReadRequest_UsesDecimalPhysicalAddress() =>
        Assert.Equal("IO:RMREQ:4227860632;1", PipeProtocol.ReadRequest(0x98));

    [Fact]
    public void WriteRequest_UsesDecimalAddressAndValue() =>
        Assert.Equal("IO:WMREQ:4227860719;1;3355443300", PipeProtocol.WriteRequest(0xEF, 3355443300u));

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Requests_RejectOffsetsOutsideEcWindow(int offset) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PipeProtocol.ReadRequest(offset));

    [Fact]
    public void Key_IsMd5OfMsiserviceNameDate()
    {
        var key = PipeProtocol.Key("M-Helper", "20261014");
        var expected = System.Security.Cryptography.MD5.HashData(Encoding.ASCII.GetBytes("msiserviceM-Helper20261014"));
        Assert.Equal(expected, key);
    }

    [Fact]
    public void EncryptDecrypt_RoundTripsWithZeroPadding()
    {
        var plain = PipeProtocol.Frame("IO:RMREQ:4227860632;1");
        var cipher = PipeProtocol.Encrypt(plain, "M-Helper", "20261014");
        Assert.Equal(0, cipher.Length % 8);
        Assert.NotEqual(plain, cipher[..plain.Length]);
        var back = PipeProtocol.Decrypt(cipher, "M-Helper", "20261014");
        Assert.Equal(plain, back[..plain.Length]);
        Assert.All(back[plain.Length..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void IsHandshakeFrame_DetectsPlainHandshake()
    {
        Assert.True(PipeProtocol.IsHandshakeFrame(PipeProtocol.Frame("HS:ACK:1.0;MSI_SERVICE_2")));
        Assert.False(PipeProtocol.IsHandshakeFrame(PipeProtocol.Frame("IO:RMRST:1;5")));
    }

    [Fact]
    public void Payload_StripsHeaderAndZeroPadding()
    {
        var bytes = PipeProtocol.Frame("IO:RMRST:1;133").Concat(new byte[3]).ToArray();
        Assert.Equal("IO:RMRST:1;133", PipeProtocol.Payload(bytes));
    }

    [Theory]
    [InlineData("IO:RMRST:1;133", 133u)]
    [InlineData("IO:RMRST:1;4294967295", 4294967295u)]
    public void ParseRead_ReturnsValue(string payload, uint expected) => Assert.Equal(expected, PipeProtocol.ParseRead(payload));

    [Theory]
    [InlineData("IO:RMRST:0;ERR")]
    [InlineData("0;ApplicationNotHandShake")]
    [InlineData("")]
    public void ParseRead_ErrorsReturnNull(string payload) => Assert.Null(PipeProtocol.ParseRead(payload));

    [Theory]
    [InlineData("IO:WMRST:1", true)]
    [InlineData("IO:WMRST:0", false)]
    [InlineData("0;ApplicationNotHandShake", false)]
    public void ParseWrite(string payload, bool ok) => Assert.Equal(ok, PipeProtocol.ParseWrite(payload));

    [Fact]
    public void WithLowByte_ReplacesOnlyTheTargetByte() =>
        Assert.Equal(0xC1C2C385u, PipeProtocol.WithLowByte(0xC1C2C305u, 0x85));

    [Fact]
    public void HandshakeAccepted_LooksForPipeName()
    {
        Assert.True(PipeProtocol.HandshakeAccepted("HS:ACK:2.0.2209.1601;MSI_SERVICE_2"));
        Assert.False(PipeProtocol.HandshakeAccepted("HS:ERR:"));
    }
}
