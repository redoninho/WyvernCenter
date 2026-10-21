using WyvernCenter.Core.Profile;
using WyvernCenter.Core.Scm;
using WyvernCenter.Core.Wmi2;

namespace WyvernCenter.Core.Safety;

/// <summary>Raw byte access for a setting, routed to SCM0 or WMI v2. No checks here; EcController owns those.</summary>
public interface IEcAccess
{
    bool IsAvailable(SettingDef s);
    int ReadRaw(SettingDef s);
    void WriteRaw(SettingDef s, int raw);
}

public sealed class EcAccess : IEcAccess
{
    private readonly IScmTransport _scm;
    private readonly Wmi2Ec? _wmi2;

    public EcAccess(IScmTransport scm, Wmi2Ec? wmi2 = null)
    {
        _scm = scm;
        _wmi2 = wmi2;
    }

    public bool IsAvailable(SettingDef s) => s.Route == Route.Scm || _wmi2 is not null;

    public int ReadRaw(SettingDef s) => s.Route switch
    {
        Route.Scm => _scm.Read(s.Class!.Value, s.Index!.Value),
        Route.Wmi2 when s.Block is { } b => ByteOf(Wmi2.ReadBlock(b.Method, b.Parameter), b),
        _ => Wmi2.ReadByte(s.EcOffset!.Value),
    };

    public void WriteRaw(SettingDef s, int raw)
    {
        switch (s.Route)
        {
            case Route.Scm: _scm.Write(s.Class!.Value, s.Index!.Value, raw); break;
            case Route.Wmi2 when s.Block is not null: throw new NotSupportedException($"{s.Name}: WMI2 blocks are read-only.");
            default: Wmi2.WriteByte(s.EcOffset!.Value, checked((byte)raw)); break;
        }
    }

    private static int ByteOf(byte[] data, Wmi2Block b) =>
        b.Byte < data.Length ? data[b.Byte] : throw new InvalidDataException($"{b.Method}({b.Parameter}) returned {data.Length} bytes");

    private Wmi2Ec Wmi2 => _wmi2 ?? throw new InvalidOperationException("WMI v2 interface not present.");
}
