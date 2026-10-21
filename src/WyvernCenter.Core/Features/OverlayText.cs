using WyvernCenter.Core.Monitoring;

namespace WyvernCenter.Core.Features;

public enum OverlayRole { Fps, Graph, Cpu, Gpu, Power, Sys }

/// <summary>One overlay line: a short label (coloured by role) and its values; <see cref="Graph"/> rows draw a
/// sparkline instead of text, <see cref="Big"/> rows use a larger font.</summary>
public sealed record OverlayLine(OverlayRole Role, string Label, string Text, IReadOnlyList<double?>? Graph = null, bool Big = false);

/// <summary>Which overlay lines are shown.</summary>
public sealed record OverlayOptions(bool Fps = true, bool Cpu = true, bool Gpu = true, bool Sys = true, bool Power = true, bool Graph = true);

/// <summary>What the overlay shows this second.</summary>
public sealed record OverlayInput(
    MonitorSample? Sample,
    FrameStats? Frames,
    string? FpsApp,
    string? Profile,
    bool GpuIdle,
    PowerTotal? Power = null,
    IReadOnlyList<double?>? FpsHistory = null,
    string? FpsError = null);

/// <summary>The in-game overlay's text. Values that aren't known are left out rather than shown as zero.</summary>
public static class OverlayText
{
    public static IReadOnlyList<OverlayLine> Lines(OverlayInput i, OverlayOptions o)
    {
        var s = i.Sample;
        var lines = new List<OverlayLine>();
        if (o.Fps)
        {
            var text = i.FpsError is { } err ? $"unavailable ({err})"
                : i.Frames is { } f ? Join3(
                    $"{f.Fps}",
                    (f.OnePercentLow is int low ? $"1% low {low} · " : "") + $"{f.AvgFrameMs:F1} ms",
                    i.FpsApp)
                : "—";
            lines.Add(new(OverlayRole.Fps, "FPS", text, Big: true));
            if (o.Graph && i.FpsError is null && i.FpsHistory is { } h && h.Count(v => v is not null) >= 2)
                lines.Add(new(OverlayRole.Graph, "", "", h));
        }
        if (o.Cpu)
            lines.Add(new(OverlayRole.Cpu, "CPU", Join(
                s?.CpuMhz is double mhz ? $"{mhz / 1000:F2} GHz" : null,
                s?.CpuLoad is double load ? $"{load:F0}%" : null,
                s?.CpuW is double w ? $"{w:F0} W" : null,
                Temp(s?.CpuTemp),
                s?.CpuFanRpm is int rpm ? $"{rpm} RPM" : null)));
        if (o.Gpu) lines.Add(new(OverlayRole.Gpu, "GPU", GpuText(s, i.GpuIdle)));
        if (o.Power && i.Power is { } p)
            lines.Add(new(OverlayRole.Power, "PWR", Join3(PowerBreakdown.ShortText(p),
                string.Join(" · ", p.Items.Where(x => x.Watts >= 0.5).Select(x => $"{x.Name} {x.Watts:F0}")), null)));
        if (o.Sys)
            lines.Add(new(OverlayRole.Sys, "SYS", Join(
                s?.RamUsedGb is double ram ? $"RAM {ram:F1} GB" : null,
                s?.BatteryPct is int pct ? $"BAT {pct}%" + (s.BatteryW is double bw && Math.Abs(bw) >= 0.05 ? $" {(bw > 0 ? "+" : "−")}{Math.Abs(bw):F1} W" : "") : null,
                i.Profile)));
        return lines;
    }

    private static string GpuText(MonitorSample? s, bool idle)
    {
        if (s?.GpuTemp is not int t || t <= 0) return "asleep";
        if (s.GpuMhz is not double mhz) return idle ? $"idle  {t}°C" : $"awake  {t}°C";
        return Join(
            $"{mhz:F0} MHz",
            s.GpuLoad is double load ? $"{load:F0}%" : null,
            s.GpuW is double w ? $"{w:F0} W" : null,
            Temp(t),
            s.GpuFanRpm is int rpm ? $"{rpm} RPM" : null);
    }

    private static string? Temp(int? t) => t is int v and > 0 and < 120 ? $"{v}°C" : null;

    private static string Join(params string?[] parts) => string.Join("  ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Groups separated by wider gaps.</summary>
    private static string Join3(params string?[] parts) => string.Join("   ", parts.Where(p => !string.IsNullOrEmpty(p)));
}
