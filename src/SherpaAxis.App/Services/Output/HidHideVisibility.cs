using System.IO;
using System.Text.Json;
using SherpaAxis.App.Services.Persistence;

namespace SherpaAxis.App.Services.Output;

public sealed record InputVisibilityResult(bool Ready, string Detail);
public sealed record HidHideOwnership(string ApplicationPath, bool RegisteredApplication, bool Activated, string[] Devices);

public sealed class HidHideVisibility(string statePath, string applicationPath,
    Func<IReadOnlyList<string>, CancellationToken, Task<string>> run, Func<bool> outputAvailable) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static string DefaultStatePath => Path.Combine(AppDataPaths.Root, "physical-pedal-visibility.json");
    public void Dispose() { gate.Dispose(); GC.SuppressFinalize(this); }

    public async Task<InputVisibilityResult> ConfigureAsync(bool enabled, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!enabled)
            {
                await ReleaseCoreAsync(token).ConfigureAwait(false);
                return new(true, "Physical pedals are visible. Bind only the virtual pedals in your game.");
            }
            if (!outputAvailable())
            {
                return new(false, "Virtual output is unavailable. Existing physical-pedal visibility was preserved; run Automatic vJoy setup.");
            }
            var owned = await ReadOwnershipAsync(token).ConfigureAwait(false);
            if (owned is not null && !string.Equals(owned.ApplicationPath, applicationPath, StringComparison.OrdinalIgnoreCase))
            { await ReleaseCoreAsync(token).ConfigureAwait(false); owned = null; }
            owned ??= new(applicationPath, false, false, []);
            var state = await run(["--inv-state", "--cloak-state", "--dev-list", "--app-list"], token).ConfigureAwait(false);
            if (HasFlag(state, "--inv-on")) { return new(false, "HidHide uses an inverted application list. Review it in HidHide Configuration Client; existing settings were preserved."); }
            if (!Entries(state, "--app-reg").Contains(applicationPath, StringComparer.OrdinalIgnoreCase))
            {
                owned = owned with { RegisteredApplication = true };
                await SaveAsync(owned, token).ConfigureAwait(false);
                await run(["--app-reg", applicationPath], token).ConfigureAwait(false);
            }
            var inventory = await run(["--dev-gaming"], token).ConfigureAwait(false);
            var targets = PedalInstances(inventory);
            if (targets.Length == 0) { return new(true, "Connect FEEL-VR pedals to enable duplicate-input protection."); }
            var hidden = Entries(state, "--dev-hide");
            if (!HasFlag(state, "--cloak-on") && hidden.Except(owned.Devices, StringComparer.OrdinalIgnoreCase).Any())
            { return new(false, "HidHide is disabled with other devices configured. Enable it in HidHide Configuration Client to avoid changing their visibility automatically."); }
            var added = targets.Except(hidden, StringComparer.OrdinalIgnoreCase).ToArray();
            owned = owned with
            {
                Devices = owned.Devices.Union(added, StringComparer.OrdinalIgnoreCase).ToArray(),
                Activated = owned.Activated || !HasFlag(state, "--cloak-on")
            };
            await SaveAsync(owned, token).ConfigureAwait(false);
            var commands = new List<string>();
            foreach (var path in added) { commands.Add("--dev-hide"); commands.Add(path); }
            if (!HasFlag(state, "--cloak-on")) { commands.Add("--cloak-on"); }
            if (commands.Count != 0) { await run(commands, token).ConfigureAwait(false); }
            var actual = await run(["--cloak-state", "--dev-list"], token).ConfigureAwait(false);
            if (!HasFlag(actual, "--cloak-on") || targets.Except(Entries(actual, "--dev-hide"), StringComparer.OrdinalIgnoreCase).Any())
            { throw new IOException("HidHide did not confirm pedal hiding. Close its configuration client and retry."); }
            return new(true, "Physical pedal joystick hidden from games. SherpaAxis keeps access for input and calibration. Reconnect the pedals if they still appear in a game.");
        }
        finally { gate.Release(); }
    }

    public async Task ReleaseAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await ReleaseCoreAsync(token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task ReleaseCoreAsync(CancellationToken token)
    {
        var owned = await ReadOwnershipAsync(token).ConfigureAwait(false);
        if (owned is null) { return; }
        var state = await run(["--dev-list", "--cloak-state"], token).ConfigureAwait(false);
        var commands = new List<string>();
        foreach (var path in owned.Devices) { commands.Add("--dev-unhide"); commands.Add(path); }
        if (owned.RegisteredApplication) { commands.Add("--app-unreg"); commands.Add(owned.ApplicationPath); }
        if (owned.Activated && !Entries(state, "--dev-hide").Except(owned.Devices, StringComparer.OrdinalIgnoreCase).Any())
        { commands.Add("--cloak-off"); }
        if (commands.Count != 0) { await run(commands, token).ConfigureAwait(false); }
        var actual = await run(["--dev-list", "--app-list"], token).ConfigureAwait(false);
        if (Entries(actual, "--dev-hide").Intersect(owned.Devices, StringComparer.OrdinalIgnoreCase).Any()
            || owned.RegisteredApplication && Entries(actual, "--app-reg").Contains(owned.ApplicationPath, StringComparer.OrdinalIgnoreCase))
        { throw new IOException("Could not restore physical pedal visibility. Close HidHide Configuration Client and retry."); }
        File.Delete(statePath);
    }

    internal static bool IsPedalInstance(string? path) => path is not null && path.StartsWith(@"HID\VID_0483&PID_A2EA&COL01\", StringComparison.OrdinalIgnoreCase)
        && path.Length <= 200 && !path.Any(character => char.IsControl(character) || character == '"');
    internal static string[] PedalInstances(string inventory)
    {
        try
        {
            using var json = JsonDocument.Parse(inventory);
            return json.RootElement.EnumerateArray().SelectMany(container => container.GetProperty("devices").EnumerateArray())
                .Where(device => device.GetProperty("present").GetBoolean() && device.GetProperty("gamingDevice").GetBoolean())
                .Select(device => device.GetProperty("deviceInstancePath").GetString() ?? "").Where(IsPedalInstance)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("HidHide returned an invalid device inventory.", failure); }
    }
    private static bool HasFlag(string text, string flag) => text.Split('\n').Any(line => line.Trim() == flag);
    private static string[] Entries(string text, string command) => text.Split('\n').Select(line => line.Trim())
        .Where(line => line.StartsWith(command + " \"", StringComparison.Ordinal) && line.EndsWith('"'))
        .Select(line => line[(command.Length + 2)..^1]).ToArray();
    private async Task<HidHideOwnership?> ReadOwnershipAsync(CancellationToken token)
    {
        if (!File.Exists(statePath)) { return null; }
        if (new FileInfo(statePath).Length > 65536) { throw new InvalidDataException("HidHide ownership record is too large."); }
        HidHideOwnership owned;
        try { owned = JsonSerializer.Deserialize<HidHideOwnership>(await File.ReadAllTextAsync(statePath, token).ConfigureAwait(false), JsonOptions) ?? throw new InvalidDataException("HidHide ownership record is empty."); }
        catch (JsonException failure) { throw new InvalidDataException("HidHide ownership record is invalid.", failure); }
        if (owned.Devices is null || owned.Devices.Any(path => !IsPedalInstance(path)) || string.IsNullOrWhiteSpace(owned.ApplicationPath) || !Path.IsPathFullyQualified(owned.ApplicationPath)
            || !string.Equals(Path.GetFileName(owned.ApplicationPath), "SherpaAxis.App.exe", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("HidHide ownership record contains unexpected devices or applications."); }
        return owned;
    }
    private Task SaveAsync(HidHideOwnership owned, CancellationToken token) => AtomicFile.WriteAsync(statePath, JsonSerializer.SerializeToUtf8Bytes(owned, JsonOptions), token: token);
}
