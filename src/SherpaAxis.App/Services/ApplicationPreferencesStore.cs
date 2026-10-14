using System.IO;
using System.Text.Json;
using SherpaAxis.App.Models;

namespace SherpaAxis.App.Services;

public interface IApplicationPreferencesStore
{
    Task<ApplicationPreferences> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ApplicationPreferences preferences, CancellationToken cancellationToken);
}

public sealed class ApplicationPreferencesStore(string path) : IApplicationPreferencesStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SherpaAxis", "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public async Task<ApplicationPreferences> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) { return new(); }
        if (new FileInfo(path).Length > 65_536) { throw new InvalidDataException("Settings file is too large."); }
        try
        {
            var preferences = JsonSerializer.Deserialize<ApplicationPreferences>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
            return preferences is { SchemaVersion: 1 } ? preferences : throw new InvalidDataException("Unsupported settings file version.");
        }
        catch (JsonException failure) { throw new InvalidDataException("Settings file could not be read.", failure); }
    }

    public async Task SaveAsync(ApplicationPreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.SchemaVersion != 1) { throw new InvalidDataException("Unsupported settings file version."); }
        await AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(preferences, JsonOptions), cancellationToken);
    }
}
