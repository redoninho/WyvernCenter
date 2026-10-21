using WyvernCenter.Core.Features;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace WyvernCenter.Core.Monitoring;

/// <summary>
/// CPU readings from Windows performance counters (no driver): RAPL power from "Energy Meter" (mW), effective clock
/// from "% Processor Performance" × "Processor Frequency", load from "% Processor Utility". Counters that don't
/// exist on a machine simply read as null.
/// </summary>
public sealed class WindowsCpuCounters : ICpuCounters, IDisposable
{
    // Opened on the first reading and closed by Release() when nothing is watching (they hold PDH resources).
    private PerformanceCounter? _pkg, _cores, _igpu, _dram, _perf, _freq, _util;
    private bool _open;
    private readonly object _lock = new();

    public CpuReading Read()
    {
        lock (_lock)
        {
            if (!_open)
            {
                _pkg = Open("Energy Meter", "Power", "RAPL_Package0_PKG");
                _cores = Open("Energy Meter", "Power", "RAPL_Package0_PP0");
                _igpu = Open("Energy Meter", "Power", "RAPL_Package0_PP1");
                _dram = Open("Energy Meter", "Power", "RAPL_Package0_DRAM");
                _perf = Open("Processor Information", "% Processor Performance", "_Total");
                _freq = Open("Processor Information", "Processor Frequency", "_Total");
                _util = Open("Processor Information", "% Processor Utility", "_Total");
                _open = true;
            }
            double? perf = Next(_perf), freq = Next(_freq);
            var mhz = perf is double p && freq is double f && f > 0 ? CpuMath.EffectiveMhz(p, f) : (double?)null;
            return new CpuReading(Watts(_pkg), Watts(_cores), Watts(_igpu), Watts(_dram), mhz, Next(_util) is double u ? Math.Min(u, 100) : null);
        }
    }

    /// <summary>Closes the counters; the next Read opens them again.</summary>
    public void Release()
    {
        lock (_lock)
        {
            foreach (var c in new[] { _pkg, _cores, _igpu, _dram, _perf, _freq, _util }) c?.Dispose();
            _pkg = _cores = _igpu = _dram = _perf = _freq = _util = null;
            if (_open) PerformanceCounter.CloseSharedResources(); // the name/help tables are the memory-heavy part
            _open = false;
        }
    }

    private static double? Watts(PerformanceCounter? c) => Next(c) is double mw ? mw / 1000.0 : null;

    private static double? Next(PerformanceCounter? c)
    {
        if (c is null) return null;
        try { return c.NextValue(); }
        catch { return null; }
    }

    private static PerformanceCounter? Open(string category, string counter, string instance)
    {
        try
        {
            var c = new PerformanceCounter(category, counter, instance, readOnly: true);
            c.NextValue(); // rate counters need a first sample
            return c;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => Release();
}

/// <summary>Charge/discharge rate from root\WMI BatteryStatus; percentage from GetSystemPowerStatus.</summary>
public sealed class WindowsBattery : IBatterySource
{
    public BatteryReading? Read()
    {
        int? pct = GetSystemPowerStatus(out var st) && st.BatteryLifePercent <= 100 ? st.BatteryLifePercent : null;
        double? watts = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT ChargeRate, DischargeRate FROM BatteryStatus");
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
                using (o)
                    watts = CpuMath.BatteryWatts(Convert.ToUInt32(o["ChargeRate"]), Convert.ToUInt32(o["DischargeRate"]));
        }
        catch
        {
            // no battery WMI provider: percentage only
        }
        return new BatteryReading(watts, pct);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

public sealed class WindowsMemory : IMemorySource
{
    public MemoryReading? Read()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        const double Gb = 1024.0 * 1024 * 1024;
        return new MemoryReading((m.TotalPhys - m.AvailPhys) / Gb, m.TotalPhys / Gb);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}

/// <summary>NVIDIA GPU via NVML (ships with the driver). Initialised lazily, on first use while the GPU is awake.</summary>
public sealed class NvmlGpu : IGpuSource, IGpuClock, IGpuReader, IDisposable
{
    static NvmlGpu() => NativeLibraries.Register(); // nvml.dll only from System32 / NVIDIA's own folder

    private IntPtr _device;
    private bool _initTried, _ok;

    private readonly object _lock = new();

    public GpuReading? Read()
    {
        lock (_lock) return ReadLocked();
    }

    private GpuReading? ReadLocked()
    {
        if (!EnsureInit()) return null;
        double? mhz = nvmlDeviceGetClockInfo(_device, 0, out var clock) == 0 ? clock : null;            // NVML_CLOCK_GRAPHICS
        double? load = nvmlDeviceGetUtilizationRates(_device, out var util) == 0 ? util.Gpu : null;
        double? watts = nvmlDeviceGetPowerUsage(_device, out var mw) == 0 ? mw / 1000.0 : null;
        return new GpuReading(mhz, load, watts);
    }

    private bool EnsureInit()
    {
        if (_initTried) return _ok;
        _initTried = true;
        try
        {
            _ok = nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out _device) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _ok = false;
        }
        return _ok;
    }

    /// <summary>True when NVIDIA's driver (nvml.dll) is installed, without initialising it (doesn't wake the GPU).</summary>
    public static bool DriverPresent
    {
        get
        {
            var h = NativeLibraries.LoadNvml();
            if (h == IntPtr.Zero) return false;
            NativeLibrary.Free(h);
            return true;
        }
    }

    /// <summary>The GPU's name, top core clock and power-limit range (wakes the GPU; call Release after). Null on failure.</summary>
    public GpuLimits? ReadLimits()
    {
        lock (_lock)
        {
            if (!EnsureInit()) return null;
            var name = new System.Text.StringBuilder(96);
            var gotName = nvmlDeviceGetName(_device, name, (uint)name.Capacity) == 0;
            if (nvmlDeviceGetMaxClockInfo(_device, 0, out var top) != 0 || top < 300) return null;   // NVML_CLOCK_GRAPHICS
            uint? Get(Func<(int, uint)> f) { var (r, v) = f(); return r == 0 ? v : null; }
            var watts = HardwareDetect.GpuMaxWatts(
                Get(() => (nvmlDeviceGetPowerManagementDefaultLimit(_device, out var v), v)),
                Get(() => (nvmlDeviceGetPowerManagementLimitConstraints(_device, out _, out var v), v)),
                Get(() => (nvmlDeviceGetEnforcedPowerLimit(_device, out var v), v)));
            return watts is int w and >= 5 ? new GpuLimits(gotName ? name.ToString() : "NVIDIA GPU", (int)top, w) : null;
        }
    }

    /// <summary>Shuts NVML down (lets an idle GPU sleep); the next Read initialises it again.</summary>
    public void Release()
    {
        lock (_lock)
        {
            if (_ok) nvmlShutdown();
            _ok = _initTried = false;
        }
    }

    public GpuWrite SetMaxClock(int mhz)
    {
        lock (_lock) return !EnsureInit() ? GpuWrite.Failed : Map(nvmlDeviceSetGpuLockedClocks(_device, MinLockMhz, (uint)mhz));
    }

    public GpuWrite ResetClocks()
    {
        lock (_lock) return !EnsureInit() ? GpuWrite.Failed : Map(nvmlDeviceResetGpuLockedClocks(_device));
    }

    /// <summary>Lower bound passed with the cap (the value tested with nvidia-smi -lgc 300,max).</summary>
    private const uint MinLockMhz = 300;

    // NVML_SUCCESS 0, NVML_ERROR_NOT_SUPPORTED 3, NVML_ERROR_NO_PERMISSION 4
    private static GpuWrite Map(int r) => r switch { 0 => GpuWrite.Ok, 3 or 4 => GpuWrite.NotSupported, _ => GpuWrite.Failed };

    [DllImport("nvml.dll")] private static extern int nvmlDeviceSetGpuLockedClocks(IntPtr device, uint minMhz, uint maxMhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceResetGpuLockedClocks(IntPtr device);

    public void Dispose() => Release();

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization { public uint Gpu, Memory; }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization util);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, System.Text.StringBuilder name, uint length);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minMw, out uint maxMw);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
}
