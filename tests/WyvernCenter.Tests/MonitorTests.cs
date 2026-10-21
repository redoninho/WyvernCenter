using WyvernCenter.Core.Monitoring;

namespace WyvernCenter.Tests;

public class MonitorTests
{
    private sealed class FakeEc : IEcSensors
    {
        public int? GpuTemp { get; set; } = 0;
        public EcReading Read() => new(70, 100, GpuTemp, 40);
    }

    private sealed class FakeCpu : ICpuCounters
    {
        public bool Throw { get; set; }
        public CpuReading Read() => Throw ? throw new InvalidOperationException("counters gone") : new CpuReading(15.4, 13.2, 0.2, 1.5, 2300, 40);
    }

    private sealed class FakeGpu : IGpuSource
    {
        public int Calls { get; private set; }
        public GpuReading? Read() { Calls++; return new GpuReading(1500, 60, 30); }
    }

    private sealed class FakeBattery : IBatterySource
    {
        public BatteryReading? Read() => new BatteryReading(-12.5, 80);
    }

    private sealed class FakeMemory : IMemorySource
    {
        public MemoryReading? Read() => new MemoryReading(9.5, 16);
    }

    [Fact]
    public void Gpu_NotQueriedWhenEcSaysOff()
    {
        var gpu = new FakeGpu();
        var m = new SystemMonitor(new FakeEc { GpuTemp = 0 }, new FakeCpu(), gpu, new FakeBattery(), new FakeMemory());
        var s = m.Sample();
        Assert.Equal(0, gpu.Calls);
        Assert.Null(s.GpuMhz);
    }

    [Fact]
    public void Gpu_QueriedWhenEcReportsTemp()
    {
        var gpu = new FakeGpu();
        var s = new SystemMonitor(new FakeEc { GpuTemp = 55 }, new FakeCpu(), gpu, new FakeBattery(), new FakeMemory()).Sample();
        Assert.Equal(1, gpu.Calls);
        Assert.Equal(1500, s.GpuMhz);
    }

    [Fact]
    public void FailingSourceDoesNotBreakOthers()
    {
        var s = new SystemMonitor(new FakeEc(), new FakeCpu { Throw = true }, new FakeGpu(), new FakeBattery(), new FakeMemory()).Sample();
        Assert.Null(s.CpuW);
        Assert.Equal(70, s.CpuTemp);
        Assert.Equal(-12.5, s.BatteryW);
        Assert.Equal(9.5, s.RamUsedGb);
    }

    [Fact]
    public void Sample_CombinesSources()
    {
        var s = new SystemMonitor(new FakeEc(), new FakeCpu(), new FakeGpu(), new FakeBattery(), new FakeMemory()).Sample();
        Assert.Equal((70, 100, 15.4, 2300.0, 40.0, 80), (s.CpuTemp, s.CpuFan, s.CpuW, s.CpuMhz, s.CpuLoad, s.BatteryPct));
    }

    [Fact]
    public void History_KeepsLastN()
    {
        var h = new MetricHistory(3);
        foreach (var v in new double?[] { 1, 2, 3, 4 }) h.Add(v);
        Assert.Equal(new double?[] { 2, 3, 4 }, h.Values);
    }

    [Fact]
    public void History_MinMaxIgnoreGaps()
    {
        var h = new MetricHistory(5);
        foreach (var v in new double?[] { 5, null, 2, 9 }) h.Add(v);
        Assert.Equal(2, h.Min);
        Assert.Equal(9, h.Max);
        Assert.Equal(9, h.Last);
    }

    [Fact]
    public void CpuMhz_FromPerformanceAndFrequency() => Assert.Equal(2300.16, CpuMath.EffectiveMhz(96.0, 2396), 2);

    [Theory]
    [InlineData(15000u, 0u, 15.0)]   // charging 15 W
    [InlineData(0u, 12500u, -12.5)]  // discharging 12.5 W
    [InlineData(0u, 0u, 0.0)]
    public void BatteryW_SignedChargeVsDischarge(uint chargeMw, uint dischargeMw, double watts) =>
        Assert.Equal(watts, CpuMath.BatteryWatts(chargeMw, dischargeMw), 3);
}
