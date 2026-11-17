namespace WyvernCenter.Core.Bridge;

/// <summary>
/// Byte access to EC RAM offsets the firmware does not expose through SCM0
/// (Cooler Boost, battery limit, Fn/Win swap). Implemented over MSI's own service.
/// </summary>
public interface IEcBridge
{
    bool Available { get; }
    /// <summary>The service is running right now: using the bridge won't have to start it (fast).</summary>
    bool Ready { get; }
    byte ReadByte(int ecOffset);
    void WriteByte(int ecOffset, byte value);
}
