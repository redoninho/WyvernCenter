using System.Text;

namespace WyvernCenter.Core.Profile;

/// <summary>The EC firmware string the GF63 firmware exposes as ASCII in MSI_Software[6..33].</summary>
public static class FirmwareId
{
    private const int Start = 6, Length = 28;

    /// <summary>e.g. "16R5EMS1.102 03112021 17:10:24" (version, date, time).</summary>
    public static string Parse(IReadOnlyList<int> software)
    {
        if (software.Count < Start + Length) return "";
        var chars = software.Skip(Start).Take(Length).Select(b => b is >= 0x20 and < 0x7F ? (char)b : '\0').ToArray();
        var text = new string(chars).Replace("\0", "");
        if (text.Length < 12) return text.Trim();
        // version(12) + date(8) + time(8) are packed with no separators.
        var sb = new StringBuilder(text[..12]);
        if (text.Length >= 20) sb.Append(' ').Append(text[12..20]);
        if (text.Length > 20) sb.Append(' ').Append(text[20..]);
        return sb.ToString().Trim();
    }

    public static string Version(string id) => id.Length >= 12 ? id[..12] : id;
}
