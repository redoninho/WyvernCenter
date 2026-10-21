using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WyvernCenter.Core.Features;

/// <summary>Windows' power mode slider (Settings → Power): best efficiency, balanced, best performance.</summary>
public enum PowerOverlay { Efficiency, Balanced, Performance }

public interface IPowerOverlay
{
    PowerOverlay? Get();
    void Set(PowerOverlay overlay);
}

/// <summary>The power mode overlay, set the same way the Windows Settings slider (and G-Helper) does.</summary>
public sealed class WindowsPowerOverlay : IPowerOverlay
{
    private static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static Guid ToGuid(PowerOverlay o) => o switch
    {
        PowerOverlay.Efficiency => Efficiency,
        PowerOverlay.Balanced => Guid.Empty,
        PowerOverlay.Performance => Performance,
        _ => throw new ArgumentOutOfRangeException(nameof(o)),
    };

    public static PowerOverlay? FromGuid(Guid g) =>
        g == Efficiency ? PowerOverlay.Efficiency : g == Guid.Empty ? PowerOverlay.Balanced : g == Performance ? PowerOverlay.Performance : null;

    public PowerOverlay? Get() => PowerGetEffectiveOverlayScheme(out var g) == 0 ? FromGuid(g) : null;

    public void Set(PowerOverlay overlay)
    {
        var err = PowerSetActiveOverlayScheme(ToGuid(overlay));
        if (err != 0) throw new Win32Exception((int)err);
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
}
