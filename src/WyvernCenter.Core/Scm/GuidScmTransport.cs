using WyvernCenter.Core.AcpiWmi;

namespace WyvernCenter.Core.Scm;

/// <summary>
/// SCM0 by firmware GUID instead of the root\WMI MSI_* classes. Those classes are defined by MSI's msiapcfg.dll (the
/// GF63's firmware carries no class definitions of its own), so they're missing on a laptop that never had MSI
/// software; the GUIDs are always there. Same instance names (ACPI\PNP0C14\0_N). Requires administrator rights.
/// </summary>
public sealed class GuidScmTransport : IScmTransport, IDisposable
{
    private readonly Dictionary<ScmClass, AcpiWmiBlock> _blocks = new();
    private readonly object _lock = new();

    public int Read(ScmClass c, int index)
    {
        lock (_lock)
            return Decode(c, Block(c).QuerySingle(Instance(c, index)));
    }

    public void Write(ScmClass c, int index, int value)
    {
        lock (_lock)
            Block(c).SetSingle(Instance(c, index), Encode(c, value));
    }

    public IReadOnlyList<int> ReadAll(ScmClass c)
    {
        lock (_lock)
        {
            var block = Block(c);
            return Enumerable.Range(0, ScmNames.Count(c)).Select(i => Decode(c, block.QuerySingle(ScmNames.InstanceName(i)))).ToArray();
        }
    }

    /// <summary>Whether the firmware has SCM0 at all (System[0] answers). Never throws.</summary>
    public bool Present()
    {
        try { Read(ScmClass.System, 0); return true; }
        catch (AcpiWmiException) { return false; }
    }

    public static int Decode(ScmClass c, byte[] data)
    {
        if (data.Length < ScmNames.ValueSize(c)) throw new InvalidDataException($"{ScmNames.WmiClass(c)}: {data.Length} bytes");
        return ScmNames.ValueSize(c) == 2 ? BitConverter.ToInt16(data, 0) : data[0];
    }

    public static byte[] Encode(ScmClass c, int value) =>
        ScmNames.ValueSize(c) == 2 ? BitConverter.GetBytes(checked((short)value)) : new[] { checked((byte)value) };

    private static string Instance(ScmClass c, int index) =>
        index >= 0 && index < ScmNames.Count(c)
            ? ScmNames.InstanceName(index)
            : throw new ArgumentOutOfRangeException(nameof(index), $"{ScmNames.WmiClass(c)} has {ScmNames.Count(c)} instances.");

    private AcpiWmiBlock Block(ScmClass c)
    {
        if (!_blocks.TryGetValue(c, out var b))
            _blocks[c] = b = AcpiWmiBlock.Open(ScmNames.FirmwareGuid(c), AcpiWmiBlock.AccessQuery | AcpiWmiBlock.AccessSet);
        return b;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var b in _blocks.Values) b.Dispose();
            _blocks.Clear();
        }
    }
}
