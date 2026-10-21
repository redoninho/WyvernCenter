namespace WyvernCenter.Core.Features;

public sealed record AutoSwitchSettings(bool Enabled, string OnAc, string OnBattery);

public static class AutoSwitch
{
    /// <summary>The profile id to use for the current power source, or null when auto-switch is off.</summary>
    public static string? ProfileFor(bool onAc, AutoSwitchSettings s) => !s.Enabled ? null : onAc ? s.OnAc : s.OnBattery;
    /// <summary>
    /// The profile to switch to when the power source really changed (previousOnAc null = start-up). Windows also
    /// sends power-status changes for battery percentage steps; those must not override a profile picked by hand.
    /// </summary>
    public static string? OnPowerChange(bool? previousOnAc, bool onAc, AutoSwitchSettings s) =>
        previousOnAc == onAc ? null : ProfileFor(onAc, s);
}
