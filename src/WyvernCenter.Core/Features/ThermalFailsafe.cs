namespace WyvernCenter.Core.Features;

public enum FailsafeAction { None, Engage, Release }

/// <summary>
/// Forces maximum cooling when CPU or GPU stays at or above <see cref="EngageAt"/> for two consecutive samples,
/// and hands control back once both are below <see cref="ReleaseBelow"/>. Readings outside 1–125 °C are ignored.
/// </summary>
public sealed class ThermalFailsafe
{
    public const int EngageAt = 95;
    public const int ReleaseBelow = 85;
    private int _hotSamples;

    public bool Engaged { get; private set; }

    /// <param name="cpuC">CPU °C, or null when the read failed.</param>
    /// <param name="gpuC">GPU °C, or null/0 when the read failed or the dGPU is powered down.</param>
    public FailsafeAction Update(int? cpuC, int? gpuC)
    {
        static bool Valid(int? t) => t is > 0 and <= 125;
        var hot = (Valid(cpuC) && cpuC >= EngageAt) || (Valid(gpuC) && gpuC >= EngageAt);

        if (!Engaged)
        {
            _hotSamples = hot ? _hotSamples + 1 : 0;
            if (_hotSamples < 2) return FailsafeAction.None;
            Engaged = true;
            _hotSamples = 0;
            return FailsafeAction.Engage;
        }

        // Never release on an unknown reading: the CPU must read valid and cool, and the GPU cool or off.
        var cool = Valid(cpuC) && cpuC < ReleaseBelow && (!Valid(gpuC) || gpuC < ReleaseBelow);
        if (!cool) return FailsafeAction.None;
        Engaged = false;
        return FailsafeAction.Release;
    }
}
