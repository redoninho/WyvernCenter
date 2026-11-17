using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WyvernCenter.Core.Bridge;

/// <summary>
/// Wire format of MSI's "MSI Foundation Service" pipe (\\.\pipe\MSI_SERVICE_2), as used by MSI's own client
/// library. A frame is a 6-byte header (00 FF + big-endian int32 total length) followed by ASCII
/// "GROUP:COMMAND:arg;arg". Handshake frames travel in clear; everything else is 3DES-ECB (zero padding)
/// with key MD5("msiservice" + clientName + yyyyMMdd). Physical addresses are EC RAM base + offset.
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = "MSI_SERVICE_2";
    public const ulong EcBase = 0xFC000800;
    public const int HeaderLength = 6;

    public static byte[] Frame(string command)
    {
        var body = Encoding.ASCII.GetBytes(command);
        var frame = new byte[HeaderLength + body.Length];
        frame[0] = 0x00;
        frame[1] = 0xFF;
        var total = frame.Length;
        frame[2] = (byte)(total >> 24);
        frame[3] = (byte)(total >> 16);
        frame[4] = (byte)(total >> 8);
        frame[5] = (byte)total;
        body.CopyTo(frame, HeaderLength);
        return frame;
    }

    public static string HandshakeRequest(string clientName, string version) => $"HS:REQ:{clientName};{version}";

    public static bool HandshakeAccepted(string payload) => payload.Contains(PipeName, StringComparison.OrdinalIgnoreCase);

    public static string ReadRequest(int ecOffset) => $"IO:RMREQ:{Address(ecOffset)};1";

    public static string WriteRequest(int ecOffset, uint dword) =>
        $"IO:WMREQ:{Address(ecOffset)};1;{dword.ToString(CultureInfo.InvariantCulture)}";

    public static bool IsHandshakeFrame(byte[] frame) =>
        frame.Length >= HeaderLength && frame[0] == 0 && frame[1] == 0xFF &&
        Encoding.ASCII.GetString(frame).IndexOf("HS:", StringComparison.OrdinalIgnoreCase) > 0;

    /// <summary>The ASCII command text of a (decrypted) frame, without header or zero padding.</summary>
    public static string Payload(byte[] frame)
    {
        if (frame.Length >= HeaderLength && frame[0] == 0 && frame[1] == 0xFF)
            frame = frame[HeaderLength..];
        return Encoding.ASCII.GetString(frame).TrimEnd('\0');
    }

    /// <summary>"IO:RMRST:1;&lt;value&gt;" → value; anything else (ERR, not handshaken, garbage) → null.</summary>
    public static uint? ParseRead(string payload)
    {
        if (!payload.StartsWith("IO:RMRST:1;", StringComparison.OrdinalIgnoreCase)) return null;
        var last = payload[(payload.LastIndexOf(';') + 1)..];
        return uint.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static bool ParseWrite(string payload) => payload.Equals("IO:WMRST:1", StringComparison.OrdinalIgnoreCase);

    /// <summary>Physical dword reads are little-endian: the low byte is the byte at the requested offset.</summary>
    public static uint WithLowByte(uint dword, byte value) => (dword & 0xFFFFFF00u) | value;

    public static byte[] Key(string clientName, string yyyymmdd) =>
        MD5.HashData(Encoding.ASCII.GetBytes("msiservice" + clientName + yyyymmdd));

    public static byte[] Encrypt(byte[] plain, string clientName, string yyyymmdd)
    {
        using var des = Cipher(clientName, yyyymmdd);
        using var t = des.CreateEncryptor();
        return t.TransformFinalBlock(plain, 0, plain.Length);
    }

    public static byte[] Decrypt(byte[] cipher, string clientName, string yyyymmdd)
    {
        using var des = Cipher(clientName, yyyymmdd);
        using var t = des.CreateDecryptor();
        return t.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    // Same construction as MSI's client and service: ECB, zero padding, 16-byte (two-key) 3DES key.
    private static TripleDES Cipher(string clientName, string yyyymmdd)
    {
        var des = TripleDES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.Zeros;
        des.Key = Key(clientName, yyyymmdd);
        return des;
    }

    private static string Address(int ecOffset)
    {
        if (ecOffset is < 0 or > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(ecOffset), "EC RAM offsets are 0x00–0xFF.");
        return (EcBase + (ulong)ecOffset).ToString(CultureInfo.InvariantCulture);
    }
}
