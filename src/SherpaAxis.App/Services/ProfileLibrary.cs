using System.IO;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Services;

public sealed class ProfileLibrary(string directory)
{
    public sealed record DeletedFile(string Path, byte[] Content);
    public async Task<DeletedFile> DeleteForUndoAsync(string path, CancellationToken token = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), DirectoryPath, StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Only profiles saved inside this library can be deleted.", nameof(path)); }
        if (new FileInfo(fullPath).Length > 1024 * 1024) { throw new InvalidDataException("The profile is too large to delete with Undo."); }
        var content = await File.ReadAllBytesAsync(fullPath, token);
        await DeleteAsync(fullPath, token);
        return new(fullPath, content);
    }
    public async Task RestoreDeletedAsync(DeletedFile deleted, CancellationToken token = default)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(deleted.Path)), DirectoryPath, StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Only profiles from this library can be restored.", nameof(deleted)); }
        var temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, deleted.Content, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, deleted.Path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
    private static readonly string[] SelectionMarkers = ["active-profile", "autoload-profile"];
    public static string DefaultDirectory => AppDataPaths.Profiles;
    public string DirectoryPath { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    public async Task<IReadOnlyList<(string Path, PedalProfile Profile)>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = new List<(string, PedalProfile)>();
        var errors = new List<string>();
        LoadErrors = errors;
        if (!Directory.Exists(DirectoryPath)) { return loaded; }
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { loaded.Add((file, await ProfileStore.LoadAsync(file, cancellationToken))); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)}: {exception.Message}");
            }
        }
        return loaded;
    }

    public async Task<string> SaveAsync(PedalProfile profile, string? existingPath = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = existingPath is not null && IsLibraryPath(existingPath)
            ? existingPath : Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".json");
        await ProfileStore.SaveAsync(path, profile, overwrite: true, cancellationToken: cancellationToken);
        await RememberAsync(path, cancellationToken);
        return path;
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), DirectoryPath, StringComparison.OrdinalIgnoreCase)
            || !fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Only profiles saved inside this library can be deleted.", nameof(path)); }
        cancellationToken.ThrowIfCancellationRequested();
        var markers = new List<string>();
        foreach (var name in SelectionMarkers)
        {
            var marker = Path.Combine(DirectoryPath, name);
            if (File.Exists(marker) && new FileInfo(marker).Length <= 4096
                && string.Equals(await File.ReadAllTextAsync(marker, cancellationToken), Path.GetFileName(fullPath), StringComparison.OrdinalIgnoreCase))
            { markers.Add(marker); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var cleared = new List<string>();
        try
        {
            foreach (var marker in markers) { File.Delete(marker); cleared.Add(marker); }
            File.Delete(fullPath);
        }
        catch
        {
            foreach (var marker in cleared)
            {
                if (File.Exists(marker)) { continue; }
                try { await AtomicFile.WriteAsync(marker, System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(fullPath)), overwrite: false, token: CancellationToken.None); }
                catch (IOException) when (File.Exists(marker)) { }
            }
            throw;
        }
    }

    public async Task RememberAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DirectoryPath) || !IsLibraryPath(path)) { return; }
        await AtomicFile.WriteAsync(Path.Combine(DirectoryPath, "active-profile"),
            System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(path)), token: cancellationToken);
    }

    public Task<string?> SelectedPathAsync(CancellationToken cancellationToken = default) =>
        ReadSelectionAsync("active-profile", cancellationToken);

    public async Task<string?> AutoLoadPathAsync(CancellationToken cancellationToken = default)
    {
        var path = await ReadSelectionAsync("autoload-profile", cancellationToken);
        return path is not null && File.Exists(path) ? path : null;
    }

    private async Task<string?> ReadSelectionAsync(string markerName, CancellationToken token)
    {
        var marker = Path.Combine(DirectoryPath, markerName);
        if (!File.Exists(marker)) { return null; }
        await using var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4096) { return null; }
        using var reader = new StreamReader(stream);
        var name = await reader.ReadToEndAsync(token);
        return name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Path.Combine(DirectoryPath, name) : null;
    }

    private bool IsLibraryPath(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), DirectoryPath, StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public async Task SetAutoLoadAsync(string? path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var marker = Path.Combine(DirectoryPath, "autoload-profile");
        if (path is null) { if (File.Exists(marker)) { File.Delete(marker); } return; }
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), DirectoryPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        { throw new ArgumentException("Auto-load requires a saved profile in this library.", nameof(path)); }
        await AtomicFile.WriteAsync(marker, System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(fullPath)), token: cancellationToken);
    }
}
