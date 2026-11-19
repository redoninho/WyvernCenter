using System.Text.Json;
using System.Text.RegularExpressions;

namespace WyvernCenter.Core.Update;

/// <summary>A GitHub release Wyvern Center can update to: its version, the three files the release workflow uploads and the
/// maintainer's signature.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string ZipName, string ZipUrl, string ZipShaUrl, string ExeShaUrl, string SigUrl);

/// <summary>
/// Reads GitHub's "latest release" answer. Everything is checked against fixed expectations (this repo, a plain vX.Y.Z
/// tag, the exact asset names and download URLs the release workflow produces), so a surprising answer is ignored, never
/// followed.
/// </summary>
public static partial class UpdateRelease
{
    public const string Repo = "redoninho/WyvernCenter";
    public const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
    public const string ExeName = "WyvernCenter.exe";

    [GeneratedRegex(@"^v(\d{1,4})\.(\d{1,4})\.(\d{1,4})$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^([0-9a-fA-F]{64})[ \t]+\*?(\S+)\s*$")]
    private static partial Regex ShaLine();

    /// <summary>The release, or null when the answer isn't a normal published, signed release with all its files.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
            if (!root.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag) return null;
            var m = TagPattern().Match(tag);
            if (!m.Success) return null;
            var version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            var plain = $"{version.Major}.{version.Minor}.{version.Build}";
            var zipName = $"WyvernCenter-{plain}.zip";
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

            string? Asset(string name)
            {
                var expected = DownloadPrefix + tag + "/" + name;
                foreach (var a in assets.EnumerateArray())
                    if (a.TryGetProperty("name", out var n) && n.GetString() == name
                        && a.TryGetProperty("browser_download_url", out var u) && u.GetString() == expected)
                        return expected;
                return null;
            }

            // No signature yet (just built, not signed): not offered; the next check sees it once it's signed.
            return Asset(zipName) is { } zip && Asset(zipName + ".sha256") is { } zipSha && Asset(ExeName + ".sha256") is { } exeSha
                   && Asset(UpdateSignature.SigName) is { } sig
                ? new ReleaseInfo(version, tag, zipName, zip, zipSha, exeSha, sig)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Parses "x.y.z" (the app's own version string).</summary>
    public static Version ParseVersion(string text)
    {
        var parts = text.Split('.');
        return new Version(int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 0, parts.Length > 2 ? int.Parse(parts[2]) : 0);
    }

    public static bool IsNewer(ReleaseInfo release, string current) => release.Version > ParseVersion(current);

    /// <summary>
    /// The hash from a "&lt;sha256&gt;  &lt;file name&gt;" checksum file (what the release workflow writes), lower case; null when
    /// the file isn't exactly one such line for <paramref name="fileName"/>.
    /// </summary>
    public static string? ParseSha256(string text, string fileName)
    {
        var lines = text.Split('\n').Select(l => l.Trim('\r', '﻿', ' ')).Where(l => l.Length > 0).ToList();
        if (lines.Count != 1) return null;
        var m = ShaLine().Match(lines[0]);
        return m.Success && m.Groups[2].Value == fileName ? m.Groups[1].Value.ToLowerInvariant() : null;
    }
}
