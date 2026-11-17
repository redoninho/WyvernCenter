using WyvernCenter.Core.Bridge;

namespace WyvernCenter.Tests;

public sealed class FakeBridge : IEcBridge
{
    public readonly byte[] Ec = new byte[256];
    public bool Available { get; set; } = true;
    /// <summary>The service is running right now (using it won't start anything).</summary>
    public bool Running { get; set; } = true;
    public bool Ready => Available && Running;
    /// <summary>When set, reads block until it is signalled (simulates a hung MSI service).</summary>
    public ManualResetEventSlim? BlockReads { get; set; }
    public ManualResetEventSlim ReadStarted { get; } = new();
    public List<(int Offset, byte Value)> Writes { get; } = new();
    public bool ThrowOnWrite { get; set; }
    public int Reads { get; private set; }

    public byte ReadByte(int ecOffset)
    {
        ReadStarted.Set();
        BlockReads?.Wait();
        Reads++;
        return Available ? Ec[ecOffset] : throw new InvalidOperationException("bridge down");
    }

    public void WriteByte(int ecOffset, byte value)
    {
        if (!Available || ThrowOnWrite) throw new InvalidOperationException("bridge down");
        Writes.Add((ecOffset, value));
        Ec[ecOffset] = value;
    }
}
