using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WyvernCenter.Core.Features;

/// <summary>
/// CPU Tier 1 on the active power scheme: processor boost mode (PERFBOOSTMODE) and maximum processor state
/// (PROCTHROTTLEMAX). The original values are saved once and can be restored.
/// </summary>
public sealed class WindowsCpuPower : ICpuPower
{
    private static Guid _subProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static Guid _perfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static Guid _procThrottleMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static Guid _procFreqMax = new("75b0ae3f-bce0-45a7-8c89-c9611c25e100");

    private readonly string _originalsPath;

    public WindowsCpuPower(string originalsPath) => _originalsPath = originalsPath;

    public static uint ToPerfBoostMode(CpuBoost b) => b switch
    {
        CpuBoost.Off => 0,
        CpuBoost.Efficient => 4,   // efficient aggressive
        CpuBoost.Aggressive => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(b)),
    };

    public static CpuBoost FromPerfBoostMode(uint v) => v switch
    {
        0 => CpuBoost.Off,
        3 or 4 or 6 => CpuBoost.Efficient,
        _ => CpuBoost.Aggressive,
    };

    public CpuBoost GetBoost(bool ac) => FromPerfBoostMode(Read(ac, _perfBoostMode));

    public void SetBoost(CpuBoost boost)
    {
        var v = ToPerfBoostMode(boost);
        Write(true, _perfBoostMode, v);
        Write(false, _perfBoostMode, v);
        Apply();
    }

    public int GetMaxState(bool ac) => (int)Read(ac, _procThrottleMax);

    public void SetMaxState(bool ac, int percent)
    {
        Write(ac, _procThrottleMax, (uint)Math.Clamp(percent, 5, 100));
        Apply();
    }

    public int GetMaxFrequency(bool ac) => (int)Read(ac, _procFreqMax);

    public void SetMaxFrequency(int mhz)
    {
        var v = (uint)Math.Max(0, mhz);
        Write(true, _procFreqMax, v);
        Write(false, _procFreqMax, v);
        Apply();
    }

    /// <summary>Records the active plan's settings once, before Wyvern Center's first write to that plan.</summary>
    public void SaveOriginalsIfMissing()
    {
        var originals = PowerPlanOriginals.Load(_originalsPath);
        var values = new PlanValues(Read(true, _perfBoostMode), Read(false, _perfBoostMode),
                                    Read(true, _procThrottleMax), Read(false, _procThrottleMax),
                                    Read(true, _procFreqMax), Read(false, _procFreqMax));
        if (originals.AddIfMissing(ActiveScheme(), values)) originals.Save();
    }

    /// <summary>Puts every plan Wyvern Center wrote to back as it was, then forgets them.</summary>
    public void RestoreOriginals()
    {
        var originals = PowerPlanOriginals.Load(_originalsPath);
        if (originals.Plans.Count == 0) return;
        var failed = new List<string>();
        foreach (var (plan, o) in originals.Plans.ToList())
        {
            try
            {
                var scheme = plan;
                Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _perfBoostMode, o.AcBoost));
                Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _perfBoostMode, o.DcBoost));
                Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _procThrottleMax, o.AcMax));
                Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _procThrottleMax, o.DcMax));
                Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _procFreqMax, o.AcFreq));
                Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref _procFreqMax, o.DcFreq));
                originals.Remove(plan);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                originals.Remove(plan); // the plan was deleted: nothing left to restore
            }
            catch (Win32Exception ex)
            {
                failed.Add($"{plan}: {ex.Message}");
            }
        }
        try { Apply(); } catch (Win32Exception) { }
        if (originals.Plans.Count == 0) originals.Clear();
        else originals.Save();
        if (failed.Count > 0) throw new InvalidOperationException("couldn't restore power plan(s) " + string.Join("; ", failed));
    }

    private static uint Read(bool ac, Guid setting)
    {
        var scheme = ActiveScheme();
        uint value;
        Check(ac
            ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref setting, out value)
            : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref setting, out value));
        return value;
    }

    private static void Write(bool ac, Guid setting, uint value)
    {
        var scheme = ActiveScheme();
        Check(ac
            ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref setting, value)
            : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref _subProcessor, ref setting, value));
    }

    /// <summary>Re-activating the scheme makes Windows apply the changed values now.</summary>
    private static void Apply()
    {
        var scheme = ActiveScheme();
        Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
    }

    private static Guid ActiveScheme()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out var ptr));
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    private static void Check(uint err)
    {
        if (err != 0) throw new Win32Exception((int)err);
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
}
