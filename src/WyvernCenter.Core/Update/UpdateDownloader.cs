using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace WyvernCenter.Core.Update;

/// <summary>
/// Asks GitHub for the latest release and downloads it. Only Wyvern Center's version goes out (in the User-Agent GitHub
/// requires); nothing about the laptop or the user. A download is used only if the zip and the exe inside it both match the
/// SHA-256 checksums published with the release, the zip holds nothing but WyvernCenter.exe, the maintainer's signature
/// covers this tag and this exe (<see cref="UpdateSignature"/>), and the exe carries the release's version.
/// </summary>
public sealed class UpdateDownloader : IDisposable
{
    /// <summary>Generous cap on the zip and the exe (the release is ~64 MB, the exe ~150 MB).</summary>
    public const long MaxBytes = 400L * 1024 * 1024;
    private const int MaxSmallBytes = 1024 * 1024;

    private readonly HttpClient http;
    private readonly IReadOnlyList<System.Security.Cryptography.ECParameters> keys;

    /// <param name="keys">Release signing keys (tests only; the app uses <see cref="UpdateSignature.ReleaseKeys"/>).</param>
    public UpdateDownloader(string currentVersion, HttpMessageHandler? handler = null, IReadOnlyList<System.Security.Cryptography.ECParameters>? keys = null)
    {
        this.keys = keys ?? UpdateSignature.ReleaseKeys;
        // Redirects to GitHub's file host are followed (never https → http); every call has its own deadline.
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WyvernCenter", currentVersion));
    }

    /// <summary>The latest published release. Throws when GitHub can't be reached or answers with something unusable.</summary>
    public async Task<ReleaseInfo> LatestAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UpdateRelease.LatestApi);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var text = await SmallTextAsync(req, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return UpdateRelease.Parse(text) ?? throw new InvalidDataException("No update ready yet: the newest release isn't signed yet (or is incomplete).");
    }

    /// <summary>
    /// Downloads and checks <paramref name="release"/> into <paramref name="stagingDir"/> (which must be admin-only for the
    /// elevated app) and returns the path of the verified exe there. Throws on any mismatch.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, string stagingDir, IProgress<int>? progress, CancellationToken ct)
    {
        var zipSha = UpdateRelease.ParseSha256(await SmallTextAsync(Get(release.ZipShaUrl), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false), release.ZipName)
            ?? throw new InvalidDataException("The release's zip checksum file is unreadable.");
        var exeSha = UpdateRelease.ParseSha256(await SmallTextAsync(Get(release.ExeShaUrl), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false), UpdateRelease.ExeName)
            ?? throw new InvalidDataException("The release's exe checksum file is unreadable.");
        var signature = await SmallTextAsync(Get(release.SigUrl), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        Directory.CreateDirectory(stagingDir);
        var zipPath = Path.Combine(stagingDir, release.ZipName);
        var exePath = Path.Combine(stagingDir, "WyvernCenter.new.exe");
        foreach (var f in new[] { zipPath, exePath }) if (File.Exists(f)) File.Delete(f);
        try
        {
            string zipHash;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromMinutes(15));
                using var resp = await http.SendAsync(Get(release.ZipUrl), HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var length = resp.Content.Headers.ContentLength;
                if (length > MaxBytes) throw new InvalidDataException("The download is far bigger than a Wyvern Center release.");
                await using var source = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                await using var target = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                zipHash = await CopyHashedAsync(source, target, length, progress, cts.Token).ConfigureAwait(false);
            }
            if (zipHash != zipSha) throw new InvalidDataException("The downloaded zip doesn't match the release's checksum.");

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.Entries.Count != 1 || zip.Entries[0].FullName != UpdateRelease.ExeName)
                    throw new InvalidDataException("The zip should hold only WyvernCenter.exe.");
                var entry = zip.Entries[0];
                if (entry.Length > MaxBytes) throw new InvalidDataException("The exe in the zip is far bigger than expected.");
                await using var source = entry.Open();
                await using var target = new FileStream(exePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var exeHash = await CopyHashedAsync(source, target, null, null, ct).ConfigureAwait(false);
                if (exeHash != exeSha) throw new InvalidDataException("The exe doesn't match the release's checksum.");
                if (!UpdateSignature.Verify(release.Tag, exeHash, signature, keys))
                    throw new InvalidDataException("The release isn't signed by Wyvern Center's maintainer; not installing it.");
            }

            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (info.FileMajorPart != release.Version.Major || info.FileMinorPart != release.Version.Minor || info.FileBuildPart != release.Version.Build)
                throw new InvalidDataException($"The downloaded exe is version {info.FileVersion}, not {release.Version}.");
            return exePath;
        }
        catch
        {
            try { if (File.Exists(exePath)) File.Delete(exePath); } catch { }
            throw;
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }
    }

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    private async Task<string> SmallTextAsync(HttpRequestMessage req, TimeSpan timeout, CancellationToken ct)
    {
        using (req)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int n;
            while ((n = await s.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + n > MaxSmallBytes) throw new InvalidDataException("GitHub's answer was unexpectedly large.");
                buffer.Write(chunk, 0, n);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    /// <summary>Copies at most <see cref="MaxBytes"/>, returning the lower-case SHA-256 of what was copied.</summary>
    private static async Task<string> CopyHashedAsync(Stream source, Stream target, long? length, IProgress<int>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        var lastPercent = -1;
        int n;
        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > MaxBytes) throw new InvalidDataException("The download is far bigger than a Wyvern Center release.");
            hash.AppendData(buffer, 0, n);
            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            if (length is > 0 && progress is not null)
            {
                var percent = (int)(total * 100 / length.Value);
                if (percent != lastPercent) progress.Report(lastPercent = percent);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void Dispose() => http.Dispose();
}

/// <summary>Swaps a verified exe in for the installed one.</summary>
public static class UpdateInstaller
{
    public const string OldName = "WyvernCenter.old.exe";

    /// <summary>
    /// Moves <paramref name="stagedExe"/> to <paramref name="installDir"/>\<paramref name="exeName"/>. The installed exe may
    /// be the one running: Windows lets a running exe be renamed (not overwritten), so it becomes WyvernCenter.old.exe and
    /// is deleted on the next start. On failure the old exe is put back.
    /// </summary>
    public static string Swap(string stagedExe, string installDir, string exeName = UpdateRelease.ExeName)
    {
        var target = Path.Combine(installDir, exeName);
        var old = Path.Combine(installDir, OldName);
        Directory.CreateDirectory(installDir);
        if (File.Exists(old)) File.Delete(old);
        var hadTarget = File.Exists(target);
        if (hadTarget) File.Move(target, old);
        try
        {
            File.Move(stagedExe, target);
        }
        catch
        {
            if (hadTarget) File.Move(old, target);
            throw;
        }
        return target;
    }

    /// <summary>Puts the previous exe back after a swap whose new exe couldn't be started.</summary>
    public static void RollBack(string installDir, string exeName = UpdateRelease.ExeName)
    {
        var target = Path.Combine(installDir, exeName);
        var old = Path.Combine(installDir, OldName);
        if (!File.Exists(old)) return;
        if (File.Exists(target)) File.Delete(target);
        File.Move(old, target);
    }

    /// <summary>Removes what an earlier update left behind (the previous exe and the download folder). Never throws.</summary>
    public static void CleanUp(string installDir, string stagingDir)
    {
        try { var old = Path.Combine(installDir, OldName); if (File.Exists(old)) File.Delete(old); } catch { }
        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
    }
}
