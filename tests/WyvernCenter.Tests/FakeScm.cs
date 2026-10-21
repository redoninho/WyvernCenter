using WyvernCenter.Core.Scm;

namespace WyvernCenter.Tests;

/// <summary>In-memory SCM0: one int per instance, sized like the real firmware.</summary>
public sealed class FakeScm : IScmTransport
{
    private readonly Dictionary<ScmClass, int[]> _values =
        Enum.GetValues<ScmClass>().ToDictionary(c => c, c => new int[ScmNames.Count(c)]);

    public List<(ScmClass Class, int Index, int Value)> Writes { get; } = new();
    public int Reads { get; private set; }
    public bool ThrowOnWrite { get; set; }
    /// <summary>When set, reads block until it is signalled (simulates slow hardware holding the EC lock).</summary>
    public ManualResetEventSlim? BlockReads { get; set; }
    public ManualResetEventSlim ReadStarted { get; } = new();
    /// <summary>When true, writes are recorded but the stored value doesn't change (EC ignored it).</summary>
    public bool WritesDontStick { get; set; }
    /// <summary>When set, writes succeed this many more times and then throw (simulates a write failing part-way).</summary>
    public int? FailAfterWrites { get; set; }

    public int this[ScmClass c, int index]
    {
        get => _values[c][index];
        set => _values[c][index] = value;
    }

    /// <summary>Reads of this instance throw this many more times (a transient WMI error).</summary>
    public (ScmClass Class, int Index, int Times)? FailReads { get; set; }

    public int Read(ScmClass c, int index)
    {
        if (FailReads is { } f && f.Class == c && f.Index == index && f.Times > 0)
        {
            FailReads = f with { Times = f.Times - 1 };
            throw new InvalidOperationException("WMI read failed");
        }
        ReadStarted.Set();
        BlockReads?.Wait();
        Reads++;
        return _values[c][index];
    }

    public void Write(ScmClass c, int index, int value)
    {
        if (ThrowOnWrite) throw new InvalidOperationException("WMI write failed");
        if (FailAfterWrites is int left)
        {
            if (left <= 0) throw new InvalidOperationException("WMI write failed");
            FailAfterWrites = left - 1;
        }
        Writes.Add((c, index, value));
        if (!WritesDontStick) _values[c][index] = value;
    }

    public IReadOnlyList<int> ReadAll(ScmClass c) => _values[c].ToArray();
}
