using WyvernCenter.Core.Scm;
using WyvernCenter.Core.Wmi2;

namespace WyvernCenter.Core.Profile;

/// <summary>
/// Where a setting lives: an SCM0 instance (up to 10th gen), or an EC offset / typed block through the WMI v2
/// firmware interface (11th gen+). Both go straight to the firmware. Bridge = a raw EC offset through MSI's own
/// Dragon Center service (only when the user switched it on; for bytes the firmware doesn't expose, e.g. 0xEF).
/// </summary>
public enum Route { Scm, Wmi2, Bridge }

/// <summary>Which MSI firmware interface a profile is written for.</summary>
public enum FirmwareInterface { Scm, Wmi2 }

/// <summary>A byte of a typed WMI v2 Get_* block (e.g. Get_Fan(0) byte 1 = CPU tachometer low byte).</summary>
public sealed record Wmi2Block(Wmi2Method Method, byte Parameter, int Byte)
{
    /// <summary>
    /// A fan-curve point: Get_Fan(1) CPU / Get_Fan(2) GPU, bytes 1-6. The only block bytes that may be written, with
    /// Set_Fan, MSI's own method for curves (it hides the EC address). Every other block is read-only.
    /// </summary>
    public bool Writable => Method == Wmi2Method.GetFan && Parameter is 1 or 2 && Byte is >= 1 and <= 6;
}

/// <summary>
/// One hardware setting. <see cref="Testable"/>: the "Verify my laptop" wizard may change it briefly (and put it
/// back) even where it isn't writable yet, to confirm it on an unconfirmed model. <see cref="Mask"/> selects the bits this setting owns in its byte; values are
/// expressed already shifted down (e.g. a 0x40 mask takes 0 or 1). Writes are whitelisted by
/// <see cref="Values"/> or the inclusive <see cref="Min"/>..<see cref="Max"/> range.
/// </summary>
public sealed record SettingDef(
    string Name,
    Route Route,
    ScmClass? Class,
    int? Index,
    int? EcOffset,
    int Mask,
    IReadOnlyDictionary<string, int>? Values,
    int? Min,
    int? Max,
    bool Verified,
    bool ReadOnly,
    int ExpectMask = 0,
    IReadOnlyList<int>? ExpectValues = null,
    Wmi2Block? Block = null,
    bool Testable = false)
{
    /// <summary>
    /// Whether a current raw byte looks like this setting (no expectation = anything). The generic profile uses it to
    /// refuse writing a model whose register at this index is something else.
    /// </summary>
    public bool LooksRight(int raw) => ExpectValues is not { Count: > 0 } || ExpectValues.Contains(raw & ExpectMask);

    public int Shift => Mask == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount(Mask);

    public bool Allows(int value) =>
        Values is { Count: > 0 } ? Values.Values.Contains(value) : Min is not null && Max is not null && value >= Min && value <= Max;
}

/// <summary>Fan-curve limits: speeds in [MinSpeed, MaxSpeed]; the point at FloorAtIndex must be at least FloorSpeed.</summary>
public sealed record CurveRules(int MinSpeed, int MaxSpeed, int FloorAtIndex, int FloorSpeed);

/// <param name="Generic">The fallback for MSI laptops without a profile: never matched by firmware.</param>
public sealed record ModelProfile(
    string Name,
    IReadOnlyList<string> Firmware,
    IReadOnlyList<string> MsiServices,
    IReadOnlyDictionary<string, SettingDef> Settings,
    CurveRules Curve,
    bool Generic = false,
    FirmwareInterface Interface = FirmwareInterface.Scm);

/// <summary>How well Wyvern Center knows this laptop.</summary>
public enum ModelSupport
{
    /// <summary>This exact EC firmware has a profile: full control.</summary>
    Supported,
    /// <summary>A profile exists for this board, but for another firmware version: changes need the experimental opt-in.</summary>
    SameBoard,
    /// <summary>An MSI firmware interface with no profile: readings, and changes only through the experimental opt-in.</summary>
    Generic,
    /// <summary>No MSI firmware interface found.</summary>
    None,
}

public sealed record ModelMatch(ModelProfile? Profile, ModelSupport Support);
