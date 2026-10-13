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
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SherpaAxis", "Profiles");
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
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
        var path = existingPath is not null && Path.GetDirectoryName(Path.GetFullPath(existingPath)) == DirectoryPath
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
        File.Delete(fullPath);
        foreach (var marker in markers) { File.Delete(marker); }
    }

    public async Task RememberAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DirectoryPath) || Path.GetDirectoryName(Path.GetFullPath(path)) != DirectoryPath) { return; }
        var temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, Path.GetFileName(path), cancellationToken);
            File.Move(temporary, Path.Combine(DirectoryPath, "active-profile"), overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    public async Task<string?> SelectedPathAsync(CancellationToken cancellationToken = default)
    {
        var marker = Path.Combine(DirectoryPath, "active-profile");
        if (!File.Exists(marker) || new FileInfo(marker).Length > 4096) { return null; }
        var name = await File.ReadAllTextAsync(marker, cancellationToken);
        return name == Path.GetFileName(name) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(DirectoryPath, name) : null;
    }

    public async Task<string?> AutoLoadPathAsync(CancellationToken cancellationToken = default)
    {
        var marker = Path.Combine(DirectoryPath, "autoload-profile");
        if (!File.Exists(marker) || new FileInfo(marker).Length > 4096) { return null; }
        var name = await File.ReadAllTextAsync(marker, cancellationToken);
        if (name != Path.GetFileName(name) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { return null; }
        var path = Path.Combine(DirectoryPath, name);
        return File.Exists(path) ? path : null;
    }
    public async Task SetAutoLoadAsync(string? path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var marker = Path.Combine(DirectoryPath, "autoload-profile");
        if (path is null) { if (File.Exists(marker)) { File.Delete(marker); } return; }
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), DirectoryPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        { throw new ArgumentException("Auto-load requires a saved profile in this library.", nameof(path)); }
        Directory.CreateDirectory(DirectoryPath);
        var temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, Path.GetFileName(fullPath), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, marker, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}
