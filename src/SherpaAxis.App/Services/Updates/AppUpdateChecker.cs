using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Persistence;

namespace SherpaAxis.App.Services.Updates;

public sealed record AppRelease(string Tag)
{
    public string DisplayVersion => Tag.StartsWith('v') || Tag.StartsWith('V') ? Tag : "v" + Tag;
    public Uri Page => new($"https://github.com/{ApplicationLinks.Repository}/releases/tag/{Uri.EscapeDataString(Tag)}");
}

public sealed class AppUpdateChecker(HttpClient client, string statePath)
{
    private sealed record State(string? Tag = null, string? NotifiedTag = null);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private State? state;
    public static AppUpdateChecker Default() => new(Client, Path.Combine(AppDataPaths.Root, "updates.json"));

    public async Task<AppRelease?> CheckAsync(CancellationToken token = default)
    {
        state ??= await ReadStateAsync(token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, ApplicationLinks.LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd($"SherpaAxis/{typeof(AppUpdateChecker).Assembly.GetName().Version!.ToString(3)}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        AppRelease? release = null;
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = new byte[1_048_577];
            var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, deadline.Token).ConfigureAwait(false);
            if (length == bytes.Length) { throw new InvalidDataException("Update metadata is too large."); }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
            { throw new InvalidDataException("Update metadata has no version tag."); }
            if (!root.TryGetProperty("draft", out var draft) || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("prerelease", out var preview) || preview.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            { throw new InvalidDataException("Update metadata has no release status."); }
            if (!draft.GetBoolean() && !preview.GetBoolean() && TryVersion(tag.GetString(), out _)) { release = new(tag.GetString()!); }
        }
        var next = state with { Tag = release?.Tag };
        await StoreAsync(next, token).ConfigureAwait(false);
        state = next;
        return release;
    }

    public async Task<bool> MarkNotifiedAsync(AppRelease release, CancellationToken token = default)
    {
        if (state is null || state.Tag != release.Tag || state.NotifiedTag == release.Tag) { return false; }
        var next = state with { NotifiedTag = release.Tag };
        await StoreAsync(next, token).ConfigureAwait(false);
        state = next;
        return true;
    }

    public static bool IsNewer(AppRelease release, Version installed) => TryVersion(release.Tag, out var candidate)
        && candidate > new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build), Math.Max(0, installed.Revision));

    internal static bool TryVersion(string? tag, out Version version)
    {
        version = new(0, 0, 0, 0);
        if (string.IsNullOrEmpty(tag) || tag.Length > 64) { return false; }
        var number = tag[0] is 'v' or 'V' ? tag[1..] : tag;
        if (number.Any(character => character != '.' && !char.IsAsciiDigit(character)) || !Version.TryParse(number, out var parsed)) { return false; }
        version = new(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    private async Task<State> ReadStateAsync(CancellationToken token)
    {
        if (!File.Exists(statePath)) { return new(); }
        if (new FileInfo(statePath).Length > 4096) { return new(); }
        try { return JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(statePath, token).ConfigureAwait(false)) ?? new(); }
        catch (JsonException) { return new(); }
    }
    private Task StoreAsync(State value, CancellationToken token) => AtomicFile.WriteAsync(statePath, JsonSerializer.SerializeToUtf8Bytes(value), token: token);
}
