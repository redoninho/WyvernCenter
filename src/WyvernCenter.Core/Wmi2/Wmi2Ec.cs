namespace WyvernCenter.Core.Wmi2;

/// <summary>
/// EC access over WMI v2: Get_Data/Set_Data read and write one EC RAM byte by offset; the typed Get_* methods
/// return MSI's fixed blocks (fan tachometers etc.).
/// </summary>
public sealed class Wmi2Ec
{
    /// <summary>
    /// Off by default (read-only beta). Turned on only for the "Verify my laptop" wizard's test writes, which the
    /// controller still limits to testable settings and puts back.
    /// </summary>
    public bool WritesEnabled { get; set; }

    private readonly IWmi2Transport _transport;

    public int Major { get; }
    public int Minor { get; }
    /// <summary>Null when Get_EC didn't answer (the interface still works for reads).</summary>
    public Wmi2EcInfo? Ec { get; }
    public string Firmware => Ec?.Firmware ?? "";

    private Wmi2Ec(IWmi2Transport transport, int major, int minor, Wmi2EcInfo? ec)
    {
        _transport = transport;
        (Major, Minor, Ec) = (major, minor, ec);
    }

    /// <summary>
    /// Null unless the firmware reports WMI v2 or later (the check MSI's own software makes before every use).
    /// Only reads: Get_WMI and Get_EC.
    /// </summary>
    public static Wmi2Ec? Detect(IWmi2Transport transport, Action<string>? log = null)
    {
        try
        {
            var version = Wmi2Protocol.Version(transport.Call(Wmi2Method.GetWmi, Wmi2Protocol.Input(0)));
            if (version is not var (major, minor))
            {
                log?.Invoke("WMI2: Get_WMI reports a version below 2");
                return null;
            }
            Wmi2EcInfo? ec = null;
            try { ec = Wmi2Protocol.EcInfo(transport.Call(Wmi2Method.GetEc, Wmi2Protocol.Input(0))); }
            catch (Exception ex) { log?.Invoke("WMI2: Get_EC failed: " + ex.Message); }
            log?.Invoke($"WMI2: version {major}.{minor}, EC {(ec is null ? "unknown" : $"{ec.Firmware} (layout {ec.EcRamMainVersion}.{ec.EcRamSubVersion}{(ec.TigerLakeClass ? ", TGL+" : "")})")}");
            return new Wmi2Ec(transport, major, minor, ec);
        }
        catch (Exception ex)
        {
            log?.Invoke("WMI2: Get_WMI failed: " + ex.Message);
            return null;
        }
    }

    public byte ReadByte(int ecOffset)
    {
        var reply = Wmi2Protocol.Parse(_transport.Call(Wmi2Method.GetData, Wmi2Protocol.Input(Offset(ecOffset))));
        if (!reply.Ok || reply.Data.Length == 0) throw new InvalidOperationException($"Get_Data(0x{ecOffset:X2}) refused (flag {reply.Flag})");
        return reply.Data[0];
    }

    public void WriteByte(int ecOffset, byte value)
    {
        if (!WritesEnabled) throw new NotSupportedException("WMI v2 writes are only allowed during \"Verify my laptop\" (read-only beta).");
        var reply = Wmi2Protocol.Parse(_transport.Call(Wmi2Method.SetData, Wmi2Protocol.Input(Offset(ecOffset), new[] { value })));
        if (!reply.Ok) throw new InvalidOperationException($"Set_Data(0x{ecOffset:X2}) refused (flag {reply.Flag})");
    }

    /// <summary>Set_Fan's payload length in MSI's own table: Get_Fan(1/2)'s first 8 payload bytes (curve points at 1-6).</summary>
    public const int FanBlockLength = 8;

    /// <summary>
    /// Changes one point of a fan curve the way MSI Center does: read Get_Fan(fan), replace the point, write the
    /// block back with Set_Fan(fan). <paramref name="fan"/> 1 = CPU, 2 = GPU; <paramref name="index"/> 1-6.
    /// </summary>
    public void WriteFanCurvePoint(byte fan, int index, byte value)
    {
        if (!WritesEnabled) throw new NotSupportedException("WMI v2 writes are only allowed during \"Verify my laptop\" (read-only beta).");
        if (fan is not (1 or 2) || index is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(index), $"Get_Fan({fan}) byte {index} isn't a curve point.");
        var block = ReadBlock(Wmi2Method.GetFan, fan);
        if (block.Length < FanBlockLength) throw new InvalidDataException($"Get_Fan({fan}) returned {block.Length} bytes");
        var data = block[..FanBlockLength];
        data[index] = value;
        var reply = Wmi2Protocol.Parse(_transport.Call(Wmi2Method.SetFan, Wmi2Protocol.Input(fan, data)));
        if (!reply.Ok) throw new InvalidOperationException($"Set_Fan({fan}) refused (flag {reply.Flag})");
    }

    /// <summary>The payload of a typed Get_* call (e.g. Get_Fan(0) = tachometers).</summary>
    public byte[] ReadBlock(Wmi2Method method, byte parameter)
    {
        if (!method.ToString().StartsWith("Get", StringComparison.Ordinal)) throw new ArgumentException("Only Get_* methods read.", nameof(method));
        var reply = Wmi2Protocol.Parse(_transport.Call(method, Wmi2Protocol.Input(parameter)));
        if (!reply.Ok) throw new InvalidOperationException($"{method}({parameter}) refused (flag {reply.Flag})");
        return reply.Data;
    }

    private static byte Offset(int ecOffset) =>
        ecOffset is >= 0 and <= 0xFF ? (byte)ecOffset : throw new ArgumentOutOfRangeException(nameof(ecOffset));
}
