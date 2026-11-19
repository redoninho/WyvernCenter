using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WyvernCenter.Core.Update;

namespace WyvernCenter.Tests;

public class UpdateReleaseTests
{
    private const string Prefix = UpdateRelease.DownloadPrefix;

    internal static string ReleaseJson(string tag = "v0.5.4", bool draft = false, bool prerelease = false, string? zipUrl = null,
        bool withExeSha = true, bool withSig = true)
    {
        var v = tag.TrimStart('v');
        var assets = new List<string>
        {
            Asset($"WyvernCenter-{v}.zip", zipUrl ?? $"{Prefix}{tag}/WyvernCenter-{v}.zip"),
            Asset($"WyvernCenter-{v}.zip.sha256", $"{Prefix}{tag}/WyvernCenter-{v}.zip.sha256"),
        };
        if (withExeSha) assets.Add(Asset("WyvernCenter.exe.sha256", $"{Prefix}{tag}/WyvernCenter.exe.sha256"));
        if (withSig) assets.Add(Asset("WyvernCenter.exe.sig", $"{Prefix}{tag}/WyvernCenter.exe.sig"));
        return $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"assets":[{{string.Join(",", assets)}}]}""";

        static string Asset(string name, string url) => $$"""{"name":"{{name}}","browser_download_url":"{{url}}"}""";
    }

    [Fact]
    public void Parses_a_normal_release()
    {
        var r = UpdateRelease.Parse(ReleaseJson())!;
        Assert.Equal(new Version(0, 5, 4), r.Version);
        Assert.Equal("WyvernCenter-0.5.4.zip", r.ZipName);
        Assert.Equal(Prefix + "v0.5.4/WyvernCenter-0.5.4.zip", r.ZipUrl);
        Assert.Equal(Prefix + "v0.5.4/WyvernCenter.exe.sha256", r.ExeShaUrl);
    }

    [Theory]
    [InlineData("0.5.4")]
    [InlineData("v0.5")]
    [InlineData("v0.5.4-beta")]
    [InlineData("v0.5.4/../x")]
    public void Ignores_unusual_tags(string tag) => Assert.Null(UpdateRelease.Parse(ReleaseJson(tag)));

    [Fact]
    public void Ignores_drafts_and_prereleases()
    {
        Assert.Null(UpdateRelease.Parse(ReleaseJson(draft: true)));
        Assert.Null(UpdateRelease.Parse(ReleaseJson(prerelease: true)));
    }

    [Fact]
    public void Ignores_downloads_from_anywhere_else() =>
        Assert.Null(UpdateRelease.Parse(ReleaseJson(zipUrl: "https://example.com/WyvernCenter-0.5.4.zip")));

    [Fact]
    public void Needs_all_three_files() => Assert.Null(UpdateRelease.Parse(ReleaseJson(withExeSha: false)));

    [Fact]
    public void Unsigned_releases_arent_offered() => Assert.Null(UpdateRelease.Parse(ReleaseJson(withSig: false)));

    [Fact]
    public void The_built_in_release_key_is_a_valid_P256_key()
    {
        using var ecdsa = ECDsa.Create(Assert.Single(UpdateSignature.ReleaseKeys));
        Assert.Equal(256, ecdsa.KeySize);
    }

    [Fact]
    public void Signature_covers_the_tag_and_the_exe()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new[] { key.ExportParameters(false) };
        var hash = new string('b', 64);
        var sig = Convert.ToBase64String(key.SignData(UpdateSignature.Message("v1.2.3", hash), HashAlgorithmName.SHA256));
        Assert.True(UpdateSignature.Verify("v1.2.3", hash, sig + "\r\n", keys));
        Assert.False(UpdateSignature.Verify("v1.2.4", hash, sig, keys));                        // moved to another version
        Assert.False(UpdateSignature.Verify("v1.2.3", new string('c', 64), sig, keys));         // moved to another exe
        Assert.False(UpdateSignature.Verify("v1.2.3", hash, sig, UpdateSignature.ReleaseKeys)); // someone else's key
        Assert.False(UpdateSignature.Verify("v1.2.3", hash, "not base64!", keys));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"message":"API rate limit exceeded"}""")]
    public void Ignores_junk(string json) => Assert.Null(UpdateRelease.Parse(json));

    [Theory]
    [InlineData("0.5.3", true)]
    [InlineData("0.5.4", false)]
    [InlineData("0.5.10", false)]
    [InlineData("0.6.0", false)]
    [InlineData("0.4.99", true)]
    public void Compares_versions(string current, bool newer) =>
        Assert.Equal(newer, UpdateRelease.IsNewer(UpdateRelease.Parse(ReleaseJson())!, current));

    [Fact]
    public void Reads_the_workflows_checksum_files()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdateRelease.ParseSha256($"﻿{hash.ToUpperInvariant()}  WyvernCenter-0.5.4.zip\r\n", "WyvernCenter-0.5.4.zip"));
        Assert.Null(UpdateRelease.ParseSha256($"{hash}  other.zip", "WyvernCenter-0.5.4.zip"));
        Assert.Null(UpdateRelease.ParseSha256($"{hash}  a.zip\n{hash}  a.zip", "a.zip"));
        Assert.Null(UpdateRelease.ParseSha256("abc  a.zip", "a.zip"));
    }
}

public class UpdateDownloaderTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "wyvern-update-test-" + Guid.NewGuid().ToString("N"));

    // A real PE file with a version resource stands in for WyvernCenter.exe: this test assembly (version 1.0.0).
    private static readonly byte[] Exe = File.ReadAllBytes(typeof(UpdateDownloaderTests).Assembly.Location);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
                using (var s = zip.CreateEntry(name).Open()) s.Write(data);
        return ms.ToArray();
    }

    private sealed class FakeGitHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(files.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static readonly ECDsa TestKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly ECParameters[] TestKeys = { TestKey.ExportParameters(false) };

    private static byte[] Sign(string tag, byte[] exe) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(TestKey.SignData(UpdateSignature.Message(tag, Sha(exe)), HashAlgorithmName.SHA256)));

    private static UpdateDownloader Downloader(FakeGitHub gh) => new("0.5.3", gh, TestKeys);

    private static (ReleaseInfo, FakeGitHub) Release(byte[] zip, string? zipSha = null, string? exeSha = null, byte[]? sig = null)
    {
        var json = UpdateReleaseTests.ReleaseJson("v1.0.0");
        var r = UpdateRelease.Parse(json)!;
        var files = new Dictionary<string, byte[]>
        {
            [UpdateRelease.LatestApi] = Encoding.UTF8.GetBytes(json),
            [r.ZipUrl] = zip,
            [r.ZipShaUrl] = Encoding.ASCII.GetBytes($"{zipSha ?? Sha(zip)}  {r.ZipName}\r\n"),
            [r.ExeShaUrl] = Encoding.ASCII.GetBytes($"{exeSha ?? Sha(Exe)}  WyvernCenter.exe\r\n"),
            [r.SigUrl] = sig ?? Sign("v1.0.0", Exe),
        };
        return (r, new FakeGitHub(files));
    }

    [Fact]
    public async Task Finds_the_latest_release_and_sends_only_the_version()
    {
        var (_, gh) = Release(Zip(("WyvernCenter.exe", Exe)));
        using var d = Downloader(gh);
        var r = await d.LatestAsync(default);
        Assert.Equal(new Version(1, 0, 0), r.Version);
        Assert.Equal("WyvernCenter/0.5.3", Assert.Single(gh.UserAgents));
    }

    [Fact]
    public async Task Downloads_and_verifies_the_exe()
    {
        var (r, gh) = Release(Zip(("WyvernCenter.exe", Exe)));
        using var d = Downloader(gh);
        var percents = new List<int>();
        var exe = await d.DownloadAsync(r, dir, new SyncProgress(percents.Add), default);
        Assert.Equal(Exe, File.ReadAllBytes(exe));
        Assert.False(File.Exists(Path.Combine(dir, r.ZipName))); // the zip is removed
        Assert.Equal(100, percents.Last());
    }

    [Fact]
    public async Task Refuses_a_zip_that_doesnt_match_its_checksum()
    {
        var (r, gh) = Release(Zip(("WyvernCenter.exe", Exe)), zipSha: new string('0', 64));
        using var d = Downloader(gh);
        await Assert.ThrowsAsync<InvalidDataException>(() => d.DownloadAsync(r, dir, null, default));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Refuses_an_exe_that_doesnt_match_its_checksum()
    {
        var (r, gh) = Release(Zip(("WyvernCenter.exe", Exe)), exeSha: new string('0', 64));
        using var d = Downloader(gh);
        await Assert.ThrowsAsync<InvalidDataException>(() => d.DownloadAsync(r, dir, null, default));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Refuses_a_release_without_the_maintainers_signature()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = Encoding.ASCII.GetBytes(Convert.ToBase64String(other.SignData(UpdateSignature.Message("v1.0.0", Sha(Exe)), HashAlgorithmName.SHA256)));
        var (r, gh) = Release(Zip(("WyvernCenter.exe", Exe)), sig: forged);
        using var d = Downloader(gh);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => d.DownloadAsync(r, dir, null, default));
        Assert.Contains("signed", ex.Message);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Refuses_an_old_signed_exe_passed_off_as_a_new_release()
    {
        var (r, gh) = Release(Zip(("WyvernCenter.exe", Exe)), sig: Sign("v0.9.0", Exe));
        using var d = Downloader(gh);
        await Assert.ThrowsAsync<InvalidDataException>(() => d.DownloadAsync(r, dir, null, default));
    }

    [Fact]
    public async Task Refuses_a_zip_with_anything_else_in_it()
    {
        var (r, gh) = Release(Zip(("WyvernCenter.exe", Exe), ("payload.dll", new byte[] { 1 })));
        using var d = Downloader(gh);
        await Assert.ThrowsAsync<InvalidDataException>(() => d.DownloadAsync(r, dir, null, default));
    }

    [Fact]
    public async Task Refuses_an_exe_of_another_version()
    {
        // Same files, but the release claims 2.0.0 while the exe is 1.0.0.
        var json = UpdateReleaseTests.ReleaseJson("v2.0.0");
        var r = UpdateRelease.Parse(json)!;
        var zip = Zip(("WyvernCenter.exe", Exe));
        var gh = new FakeGitHub(new Dictionary<string, byte[]>
        {
            [r.ZipUrl] = zip,
            [r.ZipShaUrl] = Encoding.ASCII.GetBytes($"{Sha(zip)}  {r.ZipName}"),
            [r.ExeShaUrl] = Encoding.ASCII.GetBytes($"{Sha(Exe)}  WyvernCenter.exe"),
            [r.SigUrl] = Sign("v2.0.0", Exe),
        });
        using var d = Downloader(gh);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => d.DownloadAsync(r, dir, null, default));
        Assert.Contains("not 2.0.0", ex.Message);
    }

    [Fact]
    public void Swaps_the_installed_exe_and_can_roll_back()
    {
        var install = Path.Combine(dir, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "WyvernCenter.exe"), "old");
        var staged = Path.Combine(dir, "new.exe");
        File.WriteAllText(staged, "new");

        var target = UpdateInstaller.Swap(staged, install);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, UpdateInstaller.OldName)));

        UpdateInstaller.RollBack(install);
        Assert.Equal("old", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(install, UpdateInstaller.OldName)));
    }

    [Fact]
    public void Swap_into_a_fresh_install_folder_and_clean_up()
    {
        var install = Path.Combine(dir, "fresh");
        var staging = Path.Combine(dir, "staging");
        Directory.CreateDirectory(staging);
        var staged = Path.Combine(staging, "new.exe");
        File.WriteAllText(staged, "new");
        var target = UpdateInstaller.Swap(staged, install);
        Assert.Equal("new", File.ReadAllText(target));
        File.WriteAllText(Path.Combine(install, UpdateInstaller.OldName), "left over");
        UpdateInstaller.CleanUp(install, staging);
        Assert.False(File.Exists(Path.Combine(install, UpdateInstaller.OldName)));
        Assert.False(Directory.Exists(staging));
        Assert.True(File.Exists(target));
    }

    /// <summary>Against the real GitHub release (64 MB): only when WYVERN_NET_TESTS=1.</summary>
    [Fact]
    public async Task Real_github_release_downloads_and_verifies()
    {
        if (Environment.GetEnvironmentVariable("WYVERN_NET_TESTS") != "1") return;
        using var d = new UpdateDownloader("0.0.1"); // the real release key: the latest release must be signed
        var r = await d.LatestAsync(default);
        var exe = await d.DownloadAsync(r, dir, null, default);
        Assert.True(new FileInfo(exe).Length > 50_000_000);
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; this one reports inline so the test can read it straight away.</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
