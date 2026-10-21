using WyvernCenter.Core.Features;
using WyvernCenter.Core.Monitoring;

namespace WyvernCenter.Tests;

public class PowerBreakdownTests
{
    private static MonitorSample S(double? cpuW = 22, double? dramW = 1.4, double? gpuW = null, int? gpuTemp = 0, double? batteryW = -40,
        int? cpuRpm = 3000, int? gpuRpm = 0) =>
        new(DateTime.Now, 60, 70, gpuTemp, 40, cpuW, null, null, dramW, 3000, 30, null, null, gpuW, batteryW, 70, 10, 16, cpuRpm, gpuRpm);

    private static double W(PowerTotal t, PowerPart p) => t.Items.Single(i => i.Part == p).Watts;

    [Fact]
    public void OnBattery_TotalIsTheBatteryAndOtherIsTheRest()
    {
        var t = PowerBreakdown.Compute(S(), brightnessPct: 50, keyboardLevel: 0, gpuAwake: false, onAc: false);
        Assert.True(t.TotalMeasured);
        Assert.Equal(40, t.Total, 3);
        var known = t.Items.Where(i => i.Part != PowerPart.Other).Sum(i => i.Watts);
        Assert.Equal(40 - known, W(t, PowerPart.Other), 3);
        Assert.False(t.Items.Single(i => i.Part == PowerPart.Other).Estimated);
    }

    [Fact]
    public void OnBattery_OtherNeverNegative()
    {
        var t = PowerBreakdown.Compute(S(cpuW: 45, batteryW: -30), 100, 3, false, onAc: false);
        Assert.Equal(0, W(t, PowerPart.Other));
        Assert.True(t.Total >= t.Items.Where(i => i.Part != PowerPart.Other).Sum(i => i.Watts) - 1e-9);
    }

    [Fact]
    public void OnCharger_TotalIsAnEstimate()
    {
        var t = PowerBreakdown.Compute(S(batteryW: 12), 50, 0, false, onAc: true);
        Assert.False(t.TotalMeasured);
        Assert.Equal(PowerBreakdown.OtherOnChargerW, W(t, PowerPart.Other));
        Assert.True(t.Items.Single(i => i.Part == PowerPart.Other).Estimated);
        Assert.Equal(t.Items.Sum(i => i.Watts), t.Total, 6);
    }

    [Fact]
    public void Gpu_AsleepAwakeAndUnread()
    {
        Assert.Equal(0, W(PowerBreakdown.Compute(S(), 50, 0, gpuAwake: false, onAc: true), PowerPart.Gpu));
        var read = PowerBreakdown.Compute(S(gpuW: 24.5, gpuTemp: 60), 50, 0, gpuAwake: true, onAc: true);
        Assert.Equal(24.5, W(read, PowerPart.Gpu));
        Assert.False(read.Items.Single(i => i.Part == PowerPart.Gpu).Estimated);
        var unread = PowerBreakdown.Compute(S(gpuTemp: 50), 50, 0, gpuAwake: true, onAc: true);
        Assert.Equal(PowerBreakdown.GpuIdleW, W(unread, PowerPart.Gpu));
        Assert.True(unread.Items.Single(i => i.Part == PowerPart.Gpu).Estimated);
    }

    [Fact]
    public void Estimates_ScreenKeyboardFans()
    {
        var t = PowerBreakdown.Compute(S(cpuRpm: 6000, gpuRpm: 3000), 50, 2, false, onAc: true);
        Assert.Equal(0.8 + 4.2 * 0.5, W(t, PowerPart.Screen), 3);
        Assert.Equal(0.6, W(t, PowerPart.Keyboard), 3);
        Assert.Equal(2.0 + 2.0 * 0.125, W(t, PowerPart.Fans), 3);
        Assert.Equal(3.0, W(PowerBreakdown.Compute(S(), null, 0, false, onAc: true), PowerPart.Screen), 3);
    }

    [Fact]
    public void MissingSensorsAreLeftOut()
    {
        var t = PowerBreakdown.Compute(S(cpuW: null, dramW: null, cpuRpm: null, gpuRpm: null), 50, null, false, onAc: true);
        Assert.DoesNotContain(t.Items, i => i.Part is PowerPart.Cpu or PowerPart.Memory or PowerPart.Fans or PowerPart.Keyboard);
    }

    [Fact]
    public void Text_TotalLine()
    {
        Assert.Equal("40 W · from battery", PowerBreakdown.TotalText(PowerBreakdown.Compute(S(), 50, 0, false, onAc: false)));
        Assert.StartsWith("≈ ", PowerBreakdown.TotalText(PowerBreakdown.Compute(S(batteryW: 5), 50, 0, false, onAc: true)));
        Assert.EndsWith(" W · estimated on charger", PowerBreakdown.TotalText(PowerBreakdown.Compute(S(batteryW: 5), 50, 0, false, onAc: true)));
    }

    [Fact]
    public void Gpu_UnknownStateIsNotAConfidentZero()
    {
        var unread = PowerBreakdown.Compute(S(gpuTemp: null), 50, 0, gpuAwake: null, onAc: true);
        Assert.DoesNotContain(unread.Items, i => i.Part == PowerPart.Gpu);
        var read = PowerBreakdown.Compute(S(gpuW: 31, gpuTemp: null), 50, 0, gpuAwake: null, onAc: true);
        Assert.Equal(31, W(read, PowerPart.Gpu));
    }

    [Fact]
    public void ChargerOrBatteryComesFromTheAcLine()
    {
        // On the charger with the battery assisting under load: still an estimate, never "from battery".
        var assisting = PowerBreakdown.Compute(S(batteryW: -20), 50, 0, false, onAc: true);
        Assert.False(assisting.TotalMeasured);
        Assert.EndsWith("estimated on charger", PowerBreakdown.TotalText(assisting));
        // On battery without a drain reading: an estimate, labelled as on battery.
        var noDrain = PowerBreakdown.Compute(S(batteryW: null), 50, 0, false, onAc: false);
        Assert.False(noDrain.TotalMeasured);
        Assert.EndsWith("estimated on battery", PowerBreakdown.TotalText(noDrain));
    }

    [Fact]
    public void ImplausibleReadingsAreIgnored()
    {
        var t = PowerBreakdown.Compute(S(dramW: 0, cpuRpm: 48000, gpuRpm: 3000), 50, 0, false, onAc: true);
        Assert.DoesNotContain(t.Items, i => i.Part == PowerPart.Memory);   // RAPL DRAM reading 0 = not available
        Assert.Equal(0.25, W(t, PowerPart.Fans), 3);                         // 48,000 RPM is a bad tach read
    }
}
