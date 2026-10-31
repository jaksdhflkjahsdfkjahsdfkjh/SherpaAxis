using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Persistence;

namespace SherpaAxis.App.Services.Updates;

public sealed class AppUpdateDownloader(HttpClient client, string cacheRoot, Func<string, Version>? readVersion = null)
{
    private sealed record Asset(string Name, long Size, string Hash);
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    public static string DefaultDirectory => Path.Combine(AppDataPaths.Root, "AppUpdates");
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public static AppUpdateDownloader Default() => new(Client, DefaultDirectory);

    public async Task<PreparedAppUpdate> PrepareAsync(AppRelease release, AppUpdateInstallation installation,
        IProgress<AppUpdateProgress> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!AppUpdateChecker.IsNewer(release, installation.Version)) { throw new InvalidOperationException("The selected update is not newer than this installation."); }
        AppUpdateInstallation.RequireSafeDirectory(installation.Directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        var operation = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operation);
        var complete = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            progress.Report(new("Checking update files…"));
            var metadataUri = new Uri($"https://api.github.com/repos/{ApplicationLinks.Repository}/releases/tags/{Uri.EscapeDataString(release.Tag)}");
            using var response = await OpenAsync(metadataUri, "application/vnd.github+json", deadline.Token).ConfigureAwait(false);
            var metadata = await ReadSmallAsync(response, 1024 * 1024, deadline.Token).ConfigureAwait(false);
            var name = installation.Kind == AppUpdateKind.Zip ? "SherpaAxis-win-x64.zip" : "Setup.exe";
            var (package, checksums) = ReadAssets(metadata, release.Tag, name);
            var checksumUri = AssetUri(release.Tag, checksums.Name);
            using var checksumResponse = await OpenAsync(checksumUri, "application/octet-stream", deadline.Token).ConfigureAwait(false);
            var checksumBytes = await ReadSmallAsync(checksumResponse, 65536, deadline.Token).ConfigureAwait(false);
            if (checksumBytes.LongLength != checksums.Size || Hash(checksumBytes) != checksums.Hash)
            { throw new InvalidDataException("The release checksum file could not be verified."); }
            if (ReadChecksum(checksumBytes, name) != package.Hash)
            { throw new InvalidDataException("The package hash does not match the release checksum file."); }
            var destination = Path.Combine(operation, name);
            using (var download = await OpenAsync(AssetUri(release.Tag, name), "application/octet-stream", deadline.Token).ConfigureAwait(false))
            {
                if (download.Content.Headers.ContentLength is { } contentLength && contentLength != package.Size)
                { throw new InvalidDataException("The update download has an unexpected size."); }
                await using var input = await download.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long total = 0;
                var lastPercent = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > package.Size) { throw new InvalidDataException("The update download exceeded its declared size."); }
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    var percent = (int)(total * 100 / package.Size);
                    if (percent != lastPercent) { lastPercent = percent; progress.Report(new("Downloading update…", percent)); }
                }
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                if (total != package.Size || Convert.ToHexString(hash.GetHashAndReset()) != package.Hash)
                { throw new InvalidDataException("The downloaded update failed SHA-256 verification."); }
            }
            progress.Report(new("Verifying update…"));
            await Task.Run(() =>
            {
                if (!AppUpdateChecker.TryVersion(release.Tag, out var expected)) { throw new InvalidDataException("Invalid update version."); }
                var versionReader = readVersion ?? ReadExecutableVersion;
                if (installation.Kind == AppUpdateKind.Zip)
                { ZipAppUpdate.Validate(destination, installation.Directory, operation, expected, versionReader, deadline.Token); }
                else if (Normalize(versionReader(destination)) != expected)
                { throw new InvalidDataException("The installer version does not match the selected release."); }
            }, deadline.Token).ConfigureAwait(false);
            complete = true;
            return new(release, installation, operation, destination, package.Hash);
        }
        catch (OperationCanceledException failure) when (!token.IsCancellationRequested)
        { throw new TimeoutException("The update download timed out. Try again later.", failure); }
        finally
        {
            if (!complete && Directory.Exists(operation) && Path.GetDirectoryName(operation) == root)
            { Directory.Delete(operation, recursive: true); }
        }
    }

    private async Task<HttpResponseMessage> OpenAsync(Uri uri, string accept, CancellationToken token)
    {
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (!TrustedAddress(uri)) { throw new IOException("The update redirected outside the supported GitHub download hosts."); }
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd($"SherpaAxis/{typeof(AppUpdateDownloader).Assembly.GetName().Version!.ToString(3)}");
            request.Headers.Accept.ParseAdd(accept);
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) { throw new IOException("The update download returned an invalid redirect."); }
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            { var status = (int)response.StatusCode; response.Dispose(); throw new IOException($"GitHub could not provide the update (HTTP {status})."); }
            return response;
        }
        throw new IOException("The update download redirected too many times.");
    }

    internal static bool TrustedAddress(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo) && uri.Host is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    private static Uri AssetUri(string tag, string name) => new($"https://github.com/{ApplicationLinks.Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static (Asset Package, Asset Checksums) ReadAssets(byte[] bytes, string tag, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.GetProperty("tag_name").GetString() != tag || root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            { throw new InvalidDataException("The selected release is not a published stable update."); }
            Asset Read(string filename, long maximum)
            {
                var entries = root.GetProperty("assets").EnumerateArray().Where(item => item.GetProperty("name").GetString() == filename).ToArray();
                if (entries.Length != 1) { throw new InvalidDataException("The release does not contain all required update files."); }
                var asset = entries[0];
                var size = asset.GetProperty("size").GetInt64();
                var digest = asset.GetProperty("digest").GetString();
                if (asset.GetProperty("state").GetString() != "uploaded" || size <= 0 || size > maximum || digest is not { Length: 71 }
                    || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(Uri.IsHexDigit))
                { throw new InvalidDataException("The release asset size or SHA-256 hash is unavailable."); }
                return new(filename, size, digest[7..].ToUpperInvariant());
            }
            return (Read(name, MaximumPackageBytes), Read("SHA256SUMS.txt", 65536));
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("The update release metadata is invalid.", failure); }
    }

    private static async Task<byte[]> ReadSmallAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var bytes = new byte[maximum + 1];
        var count = await input.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
        if (count > maximum) { throw new InvalidDataException("The update metadata exceeded its size limit."); }
        return bytes[..count];
    }

    private static string ReadChecksum(byte[] bytes, string filename)
    {
        string? found = null;
        foreach (var line in Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length < 67 || text[64..66] != "  " || text[66..] != filename) { continue; }
            if (found is not null || !text[..64].All(Uri.IsHexDigit)) { throw new InvalidDataException("The update checksum file contains duplicate or invalid hashes."); }
            found = text[..64].ToUpperInvariant();
        }
        return found ?? throw new InvalidDataException("The update checksum file does not include the selected package.");
    }

    private static Version ReadExecutableVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName?.Trim() is not ("SherpaAxis" or "SherpaAxis.App") || !Version.TryParse(info.ProductVersion?.Split('+')[0].Trim(), out var version))
        { throw new InvalidDataException("The update does not identify the expected SherpaAxis application."); }
        if (Path.GetFileName(path) == "SherpaAxis.App.exe")
        {
            using var input = File.OpenRead(path);
            using var image = new PEReader(input);
            if (image.PEHeaders.CoffHeader.Machine != Machine.Amd64) { throw new InvalidDataException("The ZIP update is not the Windows x64 application."); }
        }
        return version;
    }
}
