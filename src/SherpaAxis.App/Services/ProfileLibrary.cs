using System.IO;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Services;

public sealed class ProfileLibrary(string directory)
{
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
        if (!File.Exists(marker)) { return null; }
        var name = await File.ReadAllTextAsync(marker, cancellationToken);
        return name == Path.GetFileName(name) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(DirectoryPath, name) : null;
    }
}
