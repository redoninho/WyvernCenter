using WyvernCenter.Core.Safety;

namespace WyvernCenter.Core.Features;

/// <summary>
/// Who wants the fans at full speed: the thermal failsafe, the user's Max Cooling button (Wyvern's Cooler Boost,
/// done through the firmware fan curves instead of MSI's service), or both. The curves go to max when the first
/// one asks and come back only when neither does, so the two never undo each other. Callers serialise access
/// (AppController's failsafe lock).
/// </summary>
public sealed class CoolingHold
{
    private readonly FailsafeCooling _cooling;

    public CoolingHold(FailsafeCooling cooling) => _cooling = cooling;

    public bool Thermal { get; private set; }
    public bool User { get; private set; }
    /// <summary>The fans are held at max right now (by either).</summary>
    public bool Active => _cooling.Active;
    /// <summary>Max Cooling can work on this laptop (its fan curves are writable).</summary>
    public bool Available => _cooling.CanMaxFans;

    /// <returns>The writes made, and the release outcome when the hold ended (null while it continues).</returns>
    public (IReadOnlyList<SetResult> Results, FailsafeRelease? Released) SetThermal(bool on) => Set(on, User, v => Thermal = v);

    public (IReadOnlyList<SetResult> Results, FailsafeRelease? Released) SetUser(bool on) => Set(on, Thermal, v => User = v);

    /// <summary>Re-asserts full speed while held (something else may have written the curves).</summary>
    public IReadOnlyList<SetResult> Enforce() => Active ? _cooling.Enforce() : Array.Empty<SetResult>();

    /// <summary>The user changed the fans while held (a profile, a Restore): release then re-applies what they chose.</summary>
    public void UserChangedFans() => _cooling.UserChangedFans();

    private (IReadOnlyList<SetResult>, FailsafeRelease?) Set(bool on, bool other, Action<bool> store)
    {
        store(on);
        if (on)
            return (_cooling.Active ? Array.Empty<SetResult>() : _cooling.Engage(), null);
        if (other || !_cooling.Active) return (Array.Empty<SetResult>(), null);
        var released = _cooling.Release();
        return (released.Results, released);
    }
}
