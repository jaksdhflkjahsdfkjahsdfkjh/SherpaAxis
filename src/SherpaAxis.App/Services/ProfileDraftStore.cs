using System.IO;
using System.Text.Json;
using SherpaAxis.App.Models;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Services;

public sealed record ProfileDraft(Guid Id, string Name, string? SavedFileName, Dictionary<PedalAxis, AxisEditor> Editors);
public sealed record ProfileDraftSession(int Schema, IReadOnlyList<ProfileDraft> Profiles);

/// <summary>Stores unfinished editor text separately from valid, saved or applied profiles.</summary>
public sealed class ProfileDraftStore(string path)
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    public string FilePath { get; } = Path.GetFullPath(path);
    public static ProfileDraftStore Default() => new(Path.Combine(ProfileLibrary.DefaultDirectory, ".drafts", "session.json"));
    public async Task<ProfileDraftSession?> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(FilePath)) { return null; }
        ProfileDraftSession session;
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) { throw new InvalidDataException("The draft recovery file is too large."); }
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token);
        try { session = JsonSerializer.Deserialize<ProfileDraftSession>(bytes) ?? throw new InvalidDataException("The draft recovery file is empty."); }
        catch (JsonException failure) { throw new InvalidDataException("The draft recovery file is damaged.", failure); }
        Validate(session);
        return session;
    }
    private static void Validate(ProfileDraftSession session)
    {
        if (session.Schema != 1 || session.Profiles is null || session.Profiles.Count > 100
            || session.Profiles.Select(profile => profile?.Id).Distinct().Count() != session.Profiles.Count)
        { throw new InvalidDataException("The draft recovery file has an unsupported format."); }
        foreach (var profile in session.Profiles)
        {
            if (profile is null || profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 200
                || profile.SavedFileName is { } name && (name != Path.GetFileName(name) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                || profile.Editors is null || profile.Editors.Count != 3 || Enum.GetValues<PedalAxis>().Any(axis => !profile.Editors.TryGetValue(axis, out var editor) || editor is null)
                || profile.Editors.Values.Any(editor => typeof(AxisEditor).GetProperties().Where(property => property.PropertyType == typeof(string)).Any(property => property.GetValue(editor) is not string text || text.Length > 128 * 1024)))
            { throw new InvalidDataException("The draft recovery file contains invalid editor data."); }
        }
    }
    public async Task SaveAsync(ProfileDraftSession session, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Validate(session);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session);
        if (bytes.Length > MaximumBytes) { throw new InvalidDataException("Too many unsaved edits to store for recovery. Save or delete a profile."); }
        await AtomicFile.WriteAsync(FilePath, bytes, token: token);
    }
}
