namespace WyvernCenter.Core.Features;

/// <summary>Plain wording for profiles and their controls: units, and which way each setting pushes.</summary>
public static class ProfileText
{
    public const string ClockHelp =
        "The highest speed the CPU may run at. Lower = less power and less heat, but slower. " +
        "How hot it gets and how loud the fans are also depends on the fan curve: a low clock with slow fans can still run warm, a high clock with fast fans stays cool but loud.";

    public const string GpuHelp =
        "The highest clock the NVIDIA GPU may run at. Lower = less power and heat, and lower FPS in games. Applies while the GPU is in use.";

    public const string FanHelp =
        "How fast each fan runs at each temperature point (the points are set by the laptop's firmware). Drag a point up = cooler but louder. " +
        "The last point is where the fan reaches full speed.";

    public const string CycleHelp = "Whether the profile hotkey steps through this profile.";

    public static string GpuClockLabel(int? mhz) => mhz is int m ? $"{m} MHz" : $"No cap ({Presets.GpuTopMhz} MHz)";

    public const string GpuPowerHelp =
        "Keeps the GPU under this many watts by lowering its clock when needed (most laptops don't allow setting the GPU's power limit directly). Reacts within a second or two.";

    public static string GpuPowerLabel(int? w) => w is int x ? $"{x} W" : "No limit";

    public static string Gpu(Profile p) =>
        (p.GpuMaxClockMhz is int m ? $"GPU up to {m} MHz" : "GPU no cap") + " · " + (p.GpuPowerW is int w ? $"{w} W" : "no power limit");

    /// <summary>One line for the hotkey popup: CPU clock, turbo, GPU clock and power.</summary>
    public static string Summary(Profile p) =>
        $"{(p.MaxClockMhz is int c ? $"CPU up to {c / 1000.0:F1} GHz" : "CPU no limit")} · {TurboName(p.Turbo)} · {Gpu(p)}";

    public static string ClockLabel(int? mhz) => mhz is int m ? $"{m / 1000.0:F1} GHz" : $"No limit ({Presets.MaxMhz / 1000.0:F1} GHz)";

    public static string TurboName(Turbo t) => t switch { Turbo.Off => "turbo off", Turbo.Efficient => "efficient turbo", _ => "aggressive turbo" };

    public static string TurboHelp(Turbo t) => t switch
    {
        Turbo.Off => "The CPU stays at or below its base clock. Least power and heat; slower under load.",
        Turbo.Efficient => "Boosts above the base clock when there's sustained work. A good balance.",
        _ => "Boosts to full speed as soon as anything runs. Fastest and most responsive; most power and heat.",
    };

    public static string FirmwareName(FirmwareLevel f) => f switch
    {
        FirmwareLevel.SuperBattery => "MSI Super Battery", FirmwareLevel.Extreme => "MSI Extreme", _ => "MSI Normal",
    };

    public static string FirmwareHelp(FirmwareLevel f) => f switch
    {
        FirmwareLevel.SuperBattery => "MSI's lowest power limits. Saves battery, slows the CPU under load.",
        FirmwareLevel.Extreme => "MSI's highest power limits. Lets the CPU draw the most power (hot).",
        _ => "MSI's standard power limits.",
    };

    public static string WindowsName(PowerOverlay o) => o switch
    {
        PowerOverlay.Efficiency => "Windows best efficiency", PowerOverlay.Performance => "Windows best performance", _ => "Windows balanced",
    };

    public static string WindowsHelp(PowerOverlay o) => o switch
    {
        PowerOverlay.Efficiency => "Windows favours battery life: background work runs slower.",
        PowerOverlay.Performance => "Windows favours speed: apps respond fastest, uses more power.",
        _ => "Windows' default balance of speed and power.",
    };

    public static string Cpu(Profile p) =>
        $"{(p.MaxClockMhz is int m ? $"Up to {m / 1000.0:F1} GHz" : "No clock limit")} · {TurboName(p.Turbo)} · {FirmwareName(p.Firmware)} · {WindowsName(p.Windows)}";

    /// <summary>The CPU fan curve from its first to its last point (the temperatures differ per laptop).</summary>
    public static string Fans(Profile p) => $"Fans {FanPercent.Of(p.CpuCurve[0])}% → {FanPercent.Of(p.CpuCurve[5])}%";

}
