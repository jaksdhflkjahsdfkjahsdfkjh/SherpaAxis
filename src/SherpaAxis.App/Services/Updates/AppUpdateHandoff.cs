using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SherpaAxis.App.Services.Persistence;

namespace SherpaAxis.App.Services.Updates;

public interface IAppUpdateHandoff : IAsyncDisposable
{
    void Commit();
}

public sealed record AppUpdateServices(
    Func<AppUpdateInstallation> Installation,
    Func<AppRelease, AppUpdateInstallation, IProgress<AppUpdateProgress>, CancellationToken, Task<PreparedAppUpdate>> Prepare,
    Func<PreparedAppUpdate, string, CancellationToken, Task<IAppUpdateHandoff>> Start)
{
    public static AppUpdateServices Default()
    {
        var downloader = AppUpdateDownloader.Default();
        return new(AppUpdateInstallation.Current, downloader.PrepareAsync, AppUpdateHandoff.StartAsync);
    }
}

internal sealed class AppUpdateHandoff(Process worker, string operationDirectory) : IAppUpdateHandoff
{
    internal const string MutexName = @"Local\SherpaAxis.AppUpdate";
    private bool committed;
    internal static bool IsApplying
    {
        get
        {
            Mutex? active;
            try { if (!Mutex.TryOpenExisting(MutexName, out active)) { return false; } }
            catch (UnauthorizedAccessException) { return true; }
            using (active)
            {
                try { if (!active.WaitOne(0)) { return true; } }
                catch (AbandonedMutexException) { }
                active.ReleaseMutex();
                return false;
            }
        }
    }

    internal static async Task<IAppUpdateHandoff> StartAsync(PreparedAppUpdate package, string language, CancellationToken token)
    {
        AppUpdateInstallation.RequireSafeDirectory(package.Installation.Directory);
        var directory = Path.GetFullPath(package.OperationDirectory);
        if (Path.GetDirectoryName(Path.GetFullPath(package.PackagePath)) != directory) { throw new IOException("Update package does not belong to its operation folder."); }
        using var process = Process.GetCurrentProcess();
        using var user = WindowsIdentity.GetCurrent();
        if (!AppUpdateChecker.TryVersion(package.Release.Tag, out var version)) { throw new InvalidDataException("Invalid update target version."); }
        var plan = new
        {
            schemaVersion = 1,
            kind = package.Installation.Kind.ToString(),
            version = version.ToString(4),
            applicationDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.Installation.Directory)),
            packagePath = Path.GetFullPath(package.PackagePath),
            packageHash = package.PackageHash,
            parentPid = Environment.ProcessId,
            parentStartedUtc = process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            userSid = user.User?.Value ?? throw new IOException("Cannot identify the update account."),
            language
        };
        var planPath = Path.Combine(directory, "plan.json");
        await AtomicFile.WriteAsync(planPath, JsonSerializer.SerializeToUtf8Bytes(plan), token: token).ConfigureAwait(false);
        using var embedded = typeof(AppUpdateHandoff).Assembly.GetManifestResourceStream("SherpaAxis.App.ApplyAppUpdate.ps1")!;
        using var reader = new StreamReader(embedded, Encoding.UTF8);
        var script = Path.Combine(directory, "Apply-AppUpdate.ps1");
        await File.WriteAllTextAsync(script, await reader.ReadToEndAsync(token).ConfigureAwait(false), new UTF8Encoding(false), token).ConfigureAwait(false);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-PlanPath", planPath })
        { start.ArgumentList.Add(argument); }
        var worker = Process.Start(start) ?? throw new IOException("Could not start the app update helper.");
        var handoff = new AppUpdateHandoff(worker, directory);
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, "ready")))
            {
                token.ThrowIfCancellationRequested();
                if (worker.HasExited) { throw new IOException(AppUpdateResult.ReadDetail(directory) ?? "The app update helper could not prepare the update."); }
                if (deadline.Elapsed > TimeSpan.FromMinutes(2)) { throw new TimeoutException("Preparing the app update timed out. No application files were changed."); }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            return handoff;
        }
        catch { await handoff.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public void Commit() => committed = true;
    public async ValueTask DisposeAsync()
    {
        if (!committed && !worker.HasExited)
        {
            try { await File.WriteAllTextAsync(Path.Combine(operationDirectory, "abort"), "abort").ConfigureAwait(false); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!worker.HasExited) { worker.Kill(); } }
        }
        worker.Dispose();
    }
}

internal sealed record AppUpdateResult(string Version, bool Success, string Detail, bool RestartRequired)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static string? ReadDetail(string directory) => Read(Path.Combine(directory, "result.json"))?.Detail;
    internal static AppUpdateResult? FromArguments(string[] arguments)
    {
        if (arguments is not ["--update-result", var path]) { return null; }
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (Path.GetFileName(full) != "result.json" || parent is null || !Guid.TryParseExact(Path.GetFileName(parent), "N", out _)
            || !string.Equals(Path.GetDirectoryName(parent), Path.GetFullPath(AppUpdateDownloader.DefaultDirectory), StringComparison.OrdinalIgnoreCase))
        { throw new IOException("Invalid app update result location."); }
        return Read(full);
    }
    private static AppUpdateResult? Read(string path)
    {
        if (!File.Exists(path)) { return null; }
        if (new FileInfo(path).Length > 65536) { throw new InvalidDataException("App update result is too large."); }
        try { return JsonSerializer.Deserialize<AppUpdateResult>(File.ReadAllBytes(path), Options); }
        catch (JsonException failure) { throw new InvalidDataException("Invalid app update result.", failure); }
    }
}
