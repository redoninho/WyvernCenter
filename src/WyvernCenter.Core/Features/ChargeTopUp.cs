namespace WyvernCenter.Core.Features;

/// <summary>
/// Makes MSI's charge limit behave like a plain "charge to X % and hold". MSI's EC stops charging at the limit but only
/// starts again about 10 % below it (msi-ec: start and end thresholds are always 10 % apart), so plugged in at 68 % with
/// a 70 % limit the battery just sits there. When that happens the EC limit is raised just enough to start charging,
/// and put back to the user's limit once the battery reaches it.
/// </summary>
public sealed class ChargeTopUp
{
    /// <summary>MSI's built-in gap between "start charging" and "stop charging".</summary>
    public const int EcGap = 10;

    /// <summary>Top up only from 2 % below the limit: a battery sitting at the limit isn't nudged for every 1 % it drifts.</summary>
    public const int Slack = 2;

    /// <summary>The EC limit currently raised above the user's, or null when the EC holds the user's limit.</summary>
    public int? RaisedTo { get; private set; }

    /// <summary>The user changed the limit (or the EC was written from elsewhere): the EC now holds that value.</summary>
    public void Reset() => RaisedTo = null;

    /// <summary>The write from <see cref="Next"/> failed: retry putting the limit back on the next call.</summary>
    public void WriteFailed(int wrote, int target) => RaisedTo = wrote == target ? target + 1 : null;

    /// <summary>The EC limit to write now, or null for no change.</summary>
    /// <param name="target">The user's limit (50–100).</param>
    /// <param name="battery">Battery charge, percent.</param>
    /// <param name="onAc">The charger is connected.</param>
    /// <param name="charging">Windows reports the battery charging.</param>
    public int? Next(int target, int battery, bool onAc, bool charging)
    {
        if (RaisedTo is int raised)
        {
            // Reached the limit, or unplugged: the EC goes back to the user's limit (it stops charging at once).
            if (battery >= target || !onAc) { RaisedTo = null; return target; }
            return null;
        }
        if (!onAc || charging || target >= 100) return null;
        if (battery > target - Slack) return null;       // at the limit, or within 1 %: hold (no 1 % micro-charges)
        if (battery < target - EcGap) return null;       // the EC starts charging by itself down here
        var raise = Math.Min(100, battery + EcGap + 1);  // just enough that the battery is below MSI's start point
        if (raise <= target) return null;
        RaisedTo = raise;
        return raise;
    }
}
