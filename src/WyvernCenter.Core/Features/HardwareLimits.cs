namespace WyvernCenter.Core.Features;

/// <summary>The NVIDIA GPU's own limits: its top core clock and its default power limit (TGP).</summary>
public sealed record GpuLimits(string Name, int TopMhz, int MaxW);

/// <summary>
/// This machine's CPU and GPU ranges. Presets, sliders and validation are derived from them, so the same profiles
/// make sense on any MSI laptop. Gpu is null without an NVIDIA GPU (the GPU controls are then off).
/// </summary>
public sealed record HardwareLimits(int CpuBaseMhz, int CpuMaxMhz, GpuLimits? Gpu)
{
    /// <summary>Used until detection runs, or when it fails: the GF63 Thin 10SC (i5-10500H, GTX 1650 Max-Q).</summary>
    public static readonly HardwareLimits Fallback = new(2500, 4500, new GpuLimits("NVIDIA GPU", 1740, 40));

    private GpuLimits G => Gpu ?? Fallback.Gpu!;

    public bool HasGpu => Gpu is not null;

    /// <summary>Lowest CPU clock cap offered (0.1 GHz steps).</summary>
    public int CpuMinMhz => Math.Min(1000, CpuBaseMhz);
    /// <summary>GPU clock cap range in 50 MHz steps; above the cap maximum it's "no cap".</summary>
    public int GpuMinMhz => Round(G.TopMhz * 0.345, 50);
    public int GpuCapMaxMhz => (G.TopMhz - 1) / 50 * 50;
    public int GpuTopMhz => G.TopMhz;
    public int GpuMinW => Math.Max(5, (int)Math.Round(G.MaxW * 0.25, MidpointRounding.AwayFromZero));
    public int GpuMaxW => G.MaxW;

    /// <summary>A CPU cap between base (0) and top speed (1), in 0.1 GHz steps.</summary>
    public int CpuAt(double ofTurboRange) => Round(CpuBaseMhz + ofTurboRange * (CpuMaxMhz - CpuBaseMhz), 100);
    public int CpuBelowBase(double ofBase) => Math.Max(CpuMinMhz, Round(CpuBaseMhz * ofBase, 100));
    public int? GpuClock(double ofTop) => Gpu is null ? null : Math.Clamp(Round(G.TopMhz * ofTop, 50), GpuMinMhz, GpuCapMaxMhz);
    public int? GpuWatts(double ofMax) => Gpu is null ? null : Math.Clamp((int)Math.Round(G.MaxW * ofMax, MidpointRounding.AwayFromZero), GpuMinW, G.MaxW);

    private static int Round(double v, int step) => (int)Math.Round(v / step, MidpointRounding.AwayFromZero) * step;
}
