namespace WyvernCenter.Core.Monitoring;

public readonly record struct EcReading(int? CpuTemp, int? CpuFan, int? GpuTemp, int? GpuFan, int? CpuFanRpm = null, int? GpuFanRpm = null);
public readonly record struct CpuReading(double? PackageW, double? CoresW, double? IgpuW, double? DramW, double? Mhz, double? Load);
public readonly record struct GpuReading(double? Mhz, double? Load, double? Watts);
public readonly record struct BatteryReading(double? Watts, int? Percent);
public readonly record struct MemoryReading(double UsedGb, double TotalGb);

public interface IEcSensors { EcReading Read(); }
public interface ICpuCounters { CpuReading Read(); }
public interface IGpuSource { GpuReading? Read(); }
public interface IBatterySource { BatteryReading? Read(); }
public interface IMemorySource { MemoryReading? Read(); }

public sealed record MonitorSample(
    DateTime At,
    int? CpuTemp, int? CpuFan, int? GpuTemp, int? GpuFan,
    double? CpuW, double? CoresW, double? IgpuW, double? DramW, double? CpuMhz, double? CpuLoad,
    double? GpuMhz, double? GpuLoad, double? GpuW,
    double? BatteryW, int? BatteryPct,
    double? RamUsedGb, double? RamTotalGb,
    int? CpuFanRpm = null, int? GpuFanRpm = null);

/// <summary>
/// Combines all readings into one sample. Each source fails on its own without affecting the others. The NVIDIA
/// GPU is only queried while the EC reports a GPU temperature: asking a powered-down Optimus GPU would wake it.
/// </summary>
public sealed class SystemMonitor
{
    private readonly IEcSensors _ec;
    private readonly ICpuCounters _cpu;
    private readonly IGpuSource _gpu;
    private readonly IBatterySource _battery;
    private readonly IMemorySource _memory;

    public SystemMonitor(IEcSensors ec, ICpuCounters cpu, IGpuSource gpu, IBatterySource battery, IMemorySource memory)
    {
        _ec = ec;
        _cpu = cpu;
        _gpu = gpu;
        _battery = battery;
        _memory = memory;
    }

    public MonitorSample Sample()
    {
        var ec = Try<EcReading>(() => _ec.Read());
        var cpu = Try<CpuReading>(() => _cpu.Read());
        var gpu = ec?.GpuTemp is > 0 ? Try<GpuReading>(() => _gpu.Read()) : null;
        var battery = Try<BatteryReading>(() => _battery.Read());
        var memory = Try<MemoryReading>(() => _memory.Read());

        return new MonitorSample(DateTime.Now,
            ec?.CpuTemp, ec?.CpuFan, ec?.GpuTemp, ec?.GpuFan,
            cpu?.PackageW, cpu?.CoresW, cpu?.IgpuW, cpu?.DramW, cpu?.Mhz, cpu?.Load,
            gpu?.Mhz, gpu?.Load, gpu?.Watts,
            battery?.Watts, battery?.Percent,
            memory?.UsedGb, memory?.TotalGb,
            ec?.CpuFanRpm, ec?.GpuFanRpm);
    }

    private static T? Try<T>(Func<T?> read) where T : struct
    {
        try { return read(); }
        catch { return null; }
    }
}

/// <summary>Fixed-size history of one metric (oldest first). Gaps (null) are kept so graphs stay time-aligned.</summary>
public sealed class MetricHistory
{
    private readonly double?[] _buffer;
    private int _start, _count;

    public MetricHistory(int capacity) => _buffer = new double?[capacity];

    public int Capacity => _buffer.Length;

    public void Add(double? value)
    {
        if (_count < _buffer.Length)
        {
            _buffer[(_start + _count) % _buffer.Length] = value;
            _count++;
        }
        else
        {
            _buffer[_start] = value;
            _start = (_start + 1) % _buffer.Length;
        }
    }

    public double?[] Values => Enumerable.Range(0, _count).Select(i => _buffer[(_start + i) % _buffer.Length]).ToArray();
    public double? Last => _count == 0 ? null : _buffer[(_start + _count - 1) % _buffer.Length];
    public double? Min => Values.Where(v => v is not null).DefaultIfEmpty().Min();
    public double? Max => Values.Where(v => v is not null).DefaultIfEmpty().Max();
}

public static class CpuMath
{
    /// <summary>Windows reports "% Processor Performance" relative to the base "Processor Frequency" (MHz).</summary>
    public static double EffectiveMhz(double performancePercent, double baseMhz) => baseMhz * performancePercent / 100.0;

    /// <summary>Positive while charging, negative while discharging (BatteryStatus rates are in mW).</summary>
    public static double BatteryWatts(uint chargeRateMw, uint dischargeRateMw) =>
        chargeRateMw > 0 ? chargeRateMw / 1000.0 : -(dischargeRateMw / 1000.0);
}
