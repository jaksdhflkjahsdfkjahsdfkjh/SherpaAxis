using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SherpaAxis.App.Services.Setup;

namespace SherpaAxis.App.Services.Updates;

internal static class ZipAppUpdate
{
    internal static void Validate(string package, string applicationDirectory, string operationDirectory,
        Version expectedVersion, Func<string, Version> readVersion, CancellationToken token)
    {
        _ = ZipUninstall.ReadManifest(applicationDirectory);
        using var archive = ZipFile.OpenRead(package);
        if (archive.Entries.Count is 0 or > 2001 || archive.Entries.Sum(entry => entry.Length) > 2L * 1024 * 1024 * 1024)
        { throw new InvalidDataException("The ZIP update exceeds its file or size limits."); }
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName == "SherpaAxis/") { continue; }
            if (!entry.FullName.StartsWith("SherpaAxis/", StringComparison.Ordinal) || entry.FullName.Contains('\\', StringComparison.Ordinal)
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            { throw new InvalidDataException("The ZIP update contains an invalid path or filesystem link."); }
            var relative = entry.FullName["SherpaAxis/".Length..].TrimEnd('/');
            if (!SafeRelativePath(relative)) { throw new InvalidDataException("The ZIP update contains an unsafe filename."); }
            if (entry.FullName.EndsWith('/')) { continue; }
            if (!entries.TryAdd(relative, entry)) { throw new InvalidDataException("The ZIP update contains duplicate file paths."); }
        }
        if (!entries.TryGetValue(ZipUninstall.ManifestName, out var manifestEntry) || manifestEntry.Length > 1024 * 1024)
        { throw new InvalidDataException("The ZIP update has no valid uninstall manifest."); }
        using var manifestStream = manifestEntry.Open();
        ZipPackageManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ZipPackageManifest>(manifestStream) ?? throw new InvalidDataException("The ZIP update manifest is empty."); }
        catch (JsonException failure) { throw new InvalidDataException("The ZIP update manifest is invalid.", failure); }
        if (manifest.SchemaVersion != 1 || manifest.Files is not { Length: > 0 and <= 2000 } || manifest.Files.Length != entries.Count - 1)
        { throw new InvalidDataException("The ZIP update manifest does not match its package."); }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file is null || !SafeRelativePath(file.Path) || !paths.Add(file.Path) || file.Hash is not { Length: 64 }
                || !file.Hash.All(Uri.IsHexDigit) || !entries.TryGetValue(file.Path, out var entry))
            { throw new InvalidDataException("The ZIP update manifest contains invalid or duplicate files."); }
            using var stream = entry.Open();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), file.Hash, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException("A file in the ZIP update failed hash verification."); }
        }
        if (!paths.Contains("SherpaAxis.App.exe") || !paths.Contains("SherpaAxis.App.dll") || !paths.Contains("Uninstall.exe"))
        { throw new InvalidDataException("The ZIP update is missing required application files."); }
        var executable = Path.Combine(operationDirectory, "SherpaAxis.App.exe");
        using (var input = entries["SherpaAxis.App.exe"].Open())
        using (var output = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); }
        try
        {
            if (AppUpdateDownloader.Normalize(readVersion(executable)) != expectedVersion)
            { throw new InvalidDataException("The ZIP application version does not match the selected release."); }
        }
        finally { File.Delete(executable); }
    }

    internal static bool SafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\', StringComparison.Ordinal)) { return false; }
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.TrimEnd(' ', '.') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { return false; }
            var name = part.Split('.')[0].ToUpperInvariant();
            if (name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal)
                || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9') { return false; }
        }
        return true;
    }
}
