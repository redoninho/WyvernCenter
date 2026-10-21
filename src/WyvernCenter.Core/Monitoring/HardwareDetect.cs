using System.Management;
using System.Runtime.Intrinsics.X86;
using WyvernCenter.Core.Features;

namespace WyvernCenter.Core.Monitoring;

/// <summary>Finds this machine's CPU and GPU ranges (no driver): CPUID, Windows, and NVIDIA's NVML.</summary>
public static class HardwareDetect
{
    /// <summary>Base and top (turbo) MHz from CPUID leaf 0x16 (Intel since 6th gen); null where it isn't reported.</summary>
    public static (int BaseMhz, int MaxMhz)? CpuFromCpuid()
    {
        try
        {
            if (!X86Base.IsSupported || X86Base.CpuId(0, 0).Eax < 0x16) return null;
            var (eax, ebx, _, _) = X86Base.CpuId(0x16, 0);
            return (eax & 0xFFFF, ebx & 0xFFFF);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The CPU's rated clock as Windows reports it (its base clock), or null.</summary>
    public static int? CpuBaseFromWindows()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            foreach (var o in s.Get()) using (o) return Convert.ToInt32(o["MaxClockSpeed"]);
        }
        catch
        {
            // not available: the caller falls back
        }
        return null;
    }

    /// <summary>Limits worth trusting (e.g. ones cached in settings, which could be hand-edited or corrupt).</summary>
    public static bool Plausible(GpuLimits? g) => g is { TopMhz: >= 300 and <= 4000, MaxW: >= 5 and <= 400 };

    public static HardwareLimits Detect(GpuLimits? gpu) => Combine(CpuFromCpuid(), CpuBaseFromWindows(), gpu);

    /// <summary>Picks plausible CPU numbers from what was found; the top speed is estimated when only the base is known.</summary>
    public static HardwareLimits Combine((int BaseMhz, int MaxMhz)? cpuid, int? wmiBaseMhz, GpuLimits? gpu)
    {
        static bool Plausible(int mhz) => mhz is >= 800 and <= 7000;
        static int R100(double v) => (int)Math.Round(v / 100, MidpointRounding.AwayFromZero) * 100;

        int baseMhz, maxMhz;
        if (cpuid is var (b, m) && Plausible(b))
        {
            baseMhz = R100(b);
            maxMhz = Plausible(m) && m >= b ? R100(m) : R100(b * 1.8);
        }
        else if (wmiBaseMhz is int w && Plausible(w))
        {
            baseMhz = R100(w);
            maxMhz = R100(baseMhz * 1.8);
        }
        else
        {
            baseMhz = HardwareLimits.Fallback.CpuBaseMhz;
            maxMhz = HardwareLimits.Fallback.CpuMaxMhz;
        }
        return new HardwareLimits(baseMhz, Math.Max(maxMhz, baseMhz), gpu);
    }

    /// <summary>The GPU power range's top in watts: the highest limit the driver allows (laptops with Dynamic Boost
    /// report a default below it).</summary>
    public static int? GpuMaxWatts(uint? defaultMw, uint? constraintMaxMw, uint? enforcedMw)
    {
        var best = new[] { constraintMaxMw, enforcedMw, defaultMw }.Where(v => v is > 0).Max();
        return best is uint mw ? (int)Math.Round(mw / 1000.0, MidpointRounding.AwayFromZero) : null;
    }
}
