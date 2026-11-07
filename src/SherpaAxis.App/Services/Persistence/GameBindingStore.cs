using System.IO;
using System.Text.Json;

namespace SherpaAxis.App.Services.Persistence;

internal sealed record GameProfileBinding(string ProfileFileName, string[] Processes);
internal sealed record GameBindingDocument(int SchemaVersion, GameProfileBinding[] Bindings);

internal sealed class GameBindingStore(string directory)
{
    private readonly string path = Path.Combine(directory, ".game-bindings");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal async Task<GameProfileBinding[]> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(path)) { return []; }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 65_536) { throw new InvalidDataException("Game bindings file is too large."); }
        try
        {
            var document = await JsonSerializer.DeserializeAsync<GameBindingDocument>(stream, cancellationToken: token);
            if (document is not { SchemaVersion: 1, Bindings: not null }) { throw new InvalidDataException("Unsupported game bindings file."); }
            return Validate(document.Bindings);
        }
        catch (JsonException error) { throw new InvalidDataException("Game bindings could not be read.", error); }
    }

    internal Task SaveAsync(GameProfileBinding[] bindings, CancellationToken token)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new GameBindingDocument(1, Validate(bindings)), JsonOptions);
        if (content.Length > 65_536) { throw new InvalidDataException("Game bindings file is too large."); }
        return AtomicFile.WriteAsync(path, content, token: token);
    }

    internal static string NormalizeProcess(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { name = name[..^4]; }
        if (name.Length is 0 or > 120 || name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)) || name is "." or "..")
        { throw new InvalidDataException("Enter executable names only, for example MyGame.exe, without folders or wildcards."); }
        return name;
    }

    private static GameProfileBinding[] Validate(GameProfileBinding[] bindings)
    {
        if (bindings.Length > 200) { throw new InvalidDataException("Too many game bindings."); }
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return bindings.Select(binding =>
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.ProfileFileName) || binding.ProfileFileName != Path.GetFileName(binding.ProfileFileName)
                || binding.ProfileFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !binding.ProfileFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !profiles.Add(binding.ProfileFileName)
                || binding.Processes is null || binding.Processes.Length > 50)
            { throw new InvalidDataException("Invalid game profile binding."); }
            var names = binding.Processes.Select(NormalizeProcess).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Any(name => !processes.Add(name))) { throw new InvalidDataException("A game can only be bound to one profile."); }
            return new GameProfileBinding(binding.ProfileFileName, names);
        }).ToArray();
    }
}
