using WyvernCenter.Core.Monitoring;

namespace WyvernCenter.Core.Features;

public enum PowerPart { Cpu, Gpu, Memory, Screen, Keyboard, Fans, Other }

/// <summary>One part of the laptop's power draw; <see cref="Estimated"/> when there is no sensor for it.</summary>
public sealed record PowerItem(PowerPart Part, string Name, double Watts, bool Estimated);

/// <summary>All parts and their sum. <see cref="TotalMeasured"/> on battery, where the battery's drain is the real total.</summary>
public sealed record PowerTotal(IReadOnlyList<PowerItem> Items, double Total, bool TotalMeasured, bool OnAc = true);

/// <summary>
/// Where the watts go. Measured: CPU package and RAM (Intel RAPL), GPU (NVIDIA, while awake), and on battery the
/// whole laptop (battery drain). Estimated from settings: screen (brightness), keyboard backlight (level), fans (RPM).
/// On battery "Other" is what's left of the battery's drain (SSD, Wi-Fi, chipset, conversion losses); on the charger
/// there is no total sensor, so "Other" is a fixed estimate and the total is marked as an estimate.
/// </summary>
public static class PowerBreakdown
{
    /// <summary>GPU awake but not being read (idle, so Wyvern Center stopped asking it): its idle draw.</summary>
    public const double GpuIdleW = 4.0;
    public const double OtherOnChargerW = 4.0;
    /// <summary>Screen when the brightness is unknown.</summary>
    public const double ScreenDefaultW = 3.0;
    private static readonly double[] KeyboardW = { 0, 0.3, 0.6, 1.0 };

    /// <param name="gpuAwake">From the EC GPU temperature; null when that reading isn't available.</param>
    /// <param name="onAc">From Windows' AC line status: decides charger vs battery (the battery can assist on the charger).</param>
    public static PowerTotal Compute(MonitorSample s, int? brightnessPct, int? keyboardLevel, bool? gpuAwake, bool onAc)
    {
        var items = new List<PowerItem>();
        if (s.CpuW is double cpu) items.Add(new(PowerPart.Cpu, "CPU", cpu, false));
        if (s.GpuW is double gpu) items.Add(new(PowerPart.Gpu, "GPU", gpu, false));
        else if (gpuAwake == false) items.Add(new(PowerPart.Gpu, "GPU", 0, false));
        else if (gpuAwake == true) items.Add(new(PowerPart.Gpu, "GPU", GpuIdleW, true));
        // unknown GPU state and no reading: left out rather than shown as a confident 0 W
        if (s.DramW is double ram and > 0) items.Add(new(PowerPart.Memory, "RAM", ram, false));
        // AUO 15.6" 144 Hz IPS panel: backlight dominates and scales with brightness.
        items.Add(new(PowerPart.Screen, "Screen", brightnessPct is int b ? 0.8 + 4.2 * Math.Clamp(b, 0, 100) / 100.0 : ScreenDefaultW, true));
        if (keyboardLevel is int k and >= 0 and < 4) items.Add(new(PowerPart.Keyboard, "Keyboard", KeyboardW[k], true));
        if (s.CpuFanRpm is not null || s.GpuFanRpm is not null)
            items.Add(new(PowerPart.Fans, "Fans", FanW(s.CpuFanRpm) + FanW(s.GpuFanRpm), true));

        var known = items.Sum(i => i.Watts);
        if (!onAc && s.BatteryW is double bw && bw < -0.05)
        {
            var total = Math.Max(-bw, known);
            items.Add(new(PowerPart.Other, "Other", total - known, false));
            return new PowerTotal(items, total, true, onAc);
        }
        items.Add(new(PowerPart.Other, "Other", OtherOnChargerW, true));
        return new PowerTotal(items, known + OtherOnChargerW, false, onAc);
    }

    /// <summary>Above this the tachometer read was bad (the fans top out near 7,000 RPM).</summary>
    public const int MaxPlausibleRpm = 10_000;

    /// <summary>A small laptop fan: about 2 W at 6000 RPM, power rising with the cube of speed.</summary>
    private static double FanW(int? rpm) => rpm is int r and > 0 and <= MaxPlausibleRpm ? 2.0 * Math.Pow(r / 6000.0, 3) : 0;

    public static string TotalText(PowerTotal t) =>
        t.TotalMeasured ? $"{t.Total:F0} W · from battery" : $"≈ {t.Total:F0} W · estimated on {(t.OnAc ? "charger" : "battery")}";

    /// <summary>Short form for the overlay: "34 W" on battery, "≈41 W" on the charger.</summary>
    public static string ShortText(PowerTotal t) => t.TotalMeasured ? $"{t.Total:F0} W" : $"≈{t.Total:F0} W";
}
