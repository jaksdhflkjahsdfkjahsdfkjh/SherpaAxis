using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SherpaAxis.App.Services.Setup;
using SherpaAxis.App.Services.Updates;

namespace SherpaAxis.App.Tests;

public sealed class AppUpdateDownloadTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-update-download-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(directory, "cache");
    private string Application => Path.Combine(directory, "application");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private AppUpdateInstallation Installation(AppUpdateKind kind = AppUpdateKind.Setup)
    { Directory.CreateDirectory(Application); return new(Application, kind, new Version(1, 5, 0)); }

    [Fact]
    public async Task DownloadsAndVerifiesInstallerBeforeReturningItWithoutChangingTheApp()
    {
        var installation = Installation();
        await File.WriteAllTextAsync(Path.Combine(Application, "settings.txt"), "keep");
        var payload = Encoding.UTF8.GetBytes("verified fixture installer");
        using var handler = new Handler(payload);
        using var client = new HttpClient(handler);
        var versionReads = 0;
        var downloader = new AppUpdateDownloader(client, Cache, _ => { versionReads++; return new(1, 6, 0); });
        var progress = new ProgressLog();
        var ready = await downloader.PrepareAsync(new("v1.6.0"), installation, progress, CancellationToken.None);
        Assert.Equal(payload, await File.ReadAllBytesAsync(ready.PackagePath));
        Assert.Equal(Hash(payload), ready.PackageHash, ignoreCase: true);
        Assert.Equal(1, versionReads);
        Assert.Contains(progress.Values, value => value.Stage == "Downloading update…" && value.Percent == 100);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(Application, "settings.txt")));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("payload-hash")]
    [InlineData("checksum-hash")]
    [InlineData("checksum-content")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("length")]
    [InlineData("version")]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("tag")]
    [InlineData("redirect")]
    public async Task InvalidUpdatesNeverBecomeInstallableAndPartialFilesAreRemoved(string failure)
    {
        using var handler = new Handler(Encoding.UTF8.GetBytes("fixture installer")) { Failure = failure };
        using var client = new HttpClient(handler);
        var downloader = new AppUpdateDownloader(client, Cache, _ => failure == "version" ? new(1, 7, 0) : new(1, 6, 0));
        var error = await Record.ExceptionAsync(() => downloader.PrepareAsync(new("v1.6.0"), Installation(), new ProgressLog(), CancellationToken.None));
        Assert.True(error is IOException or InvalidDataException, error?.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Cache));
        Assert.DoesNotContain(handler.Requests, uri => uri.Host == "untrusted.example");
    }

    [Fact]
    public async Task CancellingADownloadKeepsTheInstalledAppAndRemovesThePartialPackage()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler(Encoding.UTF8.GetBytes("fixture installer"));
        handler.BeforePackage = token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        using var client = new HttpClient(handler);
        var downloader = new AppUpdateDownloader(client, Cache, _ => new(1, 6, 0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.PrepareAsync(new("v1.6.0"), Installation(), new ProgressLog(), cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Cache));
        Assert.True(Directory.Exists(Application));
    }

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/file?signature=example", true)]
    [InlineData("https://github.com/file", true)]
    [InlineData("http://github.com/file", false)]
    [InlineData("https://github.com.untrusted.example/file", false)]
    [InlineData("https://user@github.com/file", false)]
    [InlineData("https://github.com:8443/file", false)]
    public void DownloadRedirectsStayOnApprovedHttpsHosts(string address, bool trusted) =>
        Assert.Equal(trusted, AppUpdateDownloader.TrustedAddress(new(address)));

    [Theory]
    [InlineData(null)]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("unlisted")]
    [InlineData("inner-hash")]
    [InlineData("symlink")]
    public async Task ZipUpdatesRequireASafeCompleteManifestAndKeepTheCurrentFolder(string? failure)
    {
        var installation = Installation(AppUpdateKind.Zip);
        var oldFiles = new Dictionary<string, byte[]> { ["SherpaAxis.App.exe"] = [1], ["SherpaAxis.App.dll"] = [2], ["Uninstall.exe"] = [1] };
        foreach (var (path, content) in oldFiles) { await File.WriteAllBytesAsync(Path.Combine(Application, path), content); }
        await File.WriteAllBytesAsync(Path.Combine(Application, "uninstall-manifest.json"), Manifest(oldFiles));
        var bytes = Zip(failure);
        using var handler = new Handler(bytes, "SherpaAxis-win-x64.zip");
        using var client = new HttpClient(handler);
        var downloader = new AppUpdateDownloader(client, Cache, _ => new(1, 6, 0));
        if (failure is null)
        {
            var result = await downloader.PrepareAsync(new("v1.6.0"), installation, new ProgressLog(), CancellationToken.None);
            Assert.EndsWith(".zip", result.PackagePath, StringComparison.Ordinal);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => downloader.PrepareAsync(new("v1.6.0"), installation, new ProgressLog(), CancellationToken.None));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Cache));
        }
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(Application, "SherpaAxis.App.exe")));
        Assert.False(File.Exists(Path.Combine(directory, "escaped.txt")));
    }

    private static byte[] Manifest(Dictionary<string, byte[]> files, bool wrongHash = false) => JsonSerializer.SerializeToUtf8Bytes(
        new ZipPackageManifest(1, files.Select(pair => new ZipFileEntry(pair.Key, wrongHash && pair.Key == "SherpaAxis.App.dll" ? new string('0', 64) : Hash(pair.Value))).ToArray()));

    private static byte[] Zip(string? failure)
    {
        var files = new Dictionary<string, byte[]> { ["SherpaAxis.App.exe"] = [1, 2, 3], ["SherpaAxis.App.dll"] = [4, 5], ["Uninstall.exe"] = [1, 2, 3] };
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] content, bool link = false)
            {
                var entry = archive.CreateEntry("SherpaAxis/" + name);
                if (link) { entry.ExternalAttributes = unchecked((int)0xA0000000); }
                using var output = entry.Open(); output.Write(content);
            }
            foreach (var pair in files) { Add(pair.Key, pair.Value, failure == "symlink" && pair.Key == "SherpaAxis.App.dll"); }
            Add("uninstall-manifest.json", Manifest(files, failure == "inner-hash"));
            if (failure == "traversal") { Add("../escaped.txt", [0]); }
            if (failure == "duplicate") { Add("uninstall.EXE", [0]); }
            if (failure == "unlisted") { Add("unknown.txt", [0]); }
        }
        return buffer.ToArray();
    }

    private sealed class ProgressLog : IProgress<AppUpdateProgress>
    {
        internal List<AppUpdateProgress> Values { get; } = [];
        public void Report(AppUpdateProgress value) => Values.Add(value);
    }

    private sealed class Handler(byte[] payload, string name = "Setup.exe") : HttpMessageHandler
    {
        public string? Failure { get; init; }
        public Func<CancellationToken, Task>? BeforePackage { get; set; }
        public List<Uri> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            var checksums = Encoding.UTF8.GetBytes((Failure == "checksum-content" ? new string('0', 64) : Hash(payload)) + "  " + name + "\n");
            if (request.RequestUri!.Host == "api.github.com")
            {
                var package = new { name, size = Failure == "oversized" ? 1024L * 1024 * 1024 : Failure == "length" ? payload.LongLength + 1 : payload.LongLength, digest = "sha256:" + Hash(payload), state = "uploaded" };
                var assets = new List<object>();
                if (Failure != "missing") { assets.Add(package); }
                if (Failure == "duplicate") { assets.Add(package); }
                assets.Add(new { name = "SHA256SUMS.txt", size = checksums.Length, digest = "sha256:" + (Failure == "checksum-hash" ? new string('0', 64) : Hash(checksums)), state = "uploaded" });
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    { tag_name = Failure == "tag" ? "v1.7.0" : "v1.6.0", draft = Failure == "draft", prerelease = Failure == "prerelease", assets }))
                };
            }
            if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
            { return new(HttpStatusCode.OK) { Content = new ByteArrayContent(checksums) }; }
            if (BeforePackage is not null) { await BeforePackage(token); }
            if (Failure == "redirect") { return new(HttpStatusCode.Found) { Headers = { Location = new("https://untrusted.example/file") } }; }
            var body = payload.ToArray(); if (Failure == "payload-hash") { body[0] ^= 1; }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }

    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
}
