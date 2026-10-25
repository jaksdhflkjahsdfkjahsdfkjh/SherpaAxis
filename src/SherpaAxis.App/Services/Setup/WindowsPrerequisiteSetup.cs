using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Output;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.Bridge;

namespace SherpaAxis.App.Services.Setup;

internal sealed record PrerequisiteProgress(string Plan, Guid Run, string Message, bool Ready = false,
    bool RestartRequired = false, DateTimeOffset? Boot = null, int? ActiveProcess = null, DateTimeOffset? ProcessStarted = null);

internal static class WindowsPrerequisiteSetup
{
    private const string Plan = "vjoy-2.2.2-hidhide-1.5.230-dfuse-3.0.6";
    private const string VJoyInstaller = "vJoySetup_v2.2.2.0_Win10_Win11.exe";
    private const string HidHideInstaller = "HidHide_1.5.230_x64.exe";
    private static string Root => AppContext.BaseDirectory;
    internal static string LogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SherpaAxis", "Prerequisites");
    private static string ProgressPath => Path.Combine(LogDirectory, "status.json");
    internal static bool IsZipPackage => Directory.Exists(Path.Combine(Root, "Prerequisites"));
    internal static DateTimeOffset Boot => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    internal static PrerequisiteProgress? ReadProgress(string? path = null)
    {
        try
        {
            path ??= ProgressPath;
            if (!File.Exists(path)) { return null; }
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 16384) { throw new InvalidDataException("Prerequisite status is too large."); }
            return JsonSerializer.Deserialize<PrerequisiteProgress>(file);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static bool HasActiveInstaller(PrerequisiteProgress? status)
    {
        if (status?.ActiveProcess is not { } id) { return false; }
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited && status.ProcessStarted is { } started
                && Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) < 1;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
    }

    internal static bool IsReady()
    {
        try
        {
            var installed = Inspect();
            if (!installed.VJoy || !installed.HidHide || !installed.DfuSe) { return false; }
            var status = ReadProgress();
            if (HasActiveInstaller(status) || status?.RestartRequired == true) { return false; }
            return status is { Ready: true, Plan: Plan } || File.Exists(AutomaticVJoy.DefaultPath);
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure)) { return false; }
    }

    private static Prerequisites Inspect()
    {
        var hidHide = false;
        try { _ = WindowsHidHide.FindCli(); hidHide = true; }
        catch (FileNotFoundException) { }
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        var dfu = Directory.Exists(store) && Directory.EnumerateDirectories(store, "sttube.inf_*").Any();
        return new(File.Exists(VJoyController.DefaultLibraryPath), hidHide, dfu);
    }

    internal static void VerifyPayload(string root)
    {
        static void Verify(string path, string expected)
        {
            using var file = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException("A bundled setup file could not be verified. Extract a fresh SherpaAxis ZIP."); }
        }
        Verify(Path.Combine(root, "Prerequisites", VJoyInstaller), "EF569A3105CD301B89580F18F60C66B339E95296ACF2C0DFCAF4B4BBF8AB68FE");
        Verify(Path.Combine(root, "Prerequisites", HidHideInstaller), "F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6");
        using var manifest = typeof(WindowsPrerequisiteSetup).Assembly.GetManifestResourceStream("SherpaAxis.App.DfuSeHashes.json")!;
        foreach (var (name, hash) in JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)!)
        { Verify(Path.Combine(root, "Tools", "DfuSe", name), hash); }
    }

    internal static async Task<bool> RunElevatedAsync(IProgress<string> progress)
    {
        var previous = ReadProgress();
        if (HasActiveInstaller(previous))
        { throw new IOException("A driver installer is still running. Wait for it to finish before retrying; do not start another installation."); }
        if (previous is { RestartRequired: true, Boot: { } boot } && Math.Abs((Boot - boot).TotalSeconds) < 60)
        { throw new IOException("Restart Windows, then open SherpaAxis again to finish device setup."); }
        await Task.Run(() => { VerifyPayload(Root); VendorControl.RequireClosed(); }).ConfigureAwait(false);
        var run = Guid.NewGuid();
        var start = new ProcessStartInfo(Path.Combine(Root, "SherpaAxis.App.exe"))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Root };
        start.ArgumentList.Add("--prepare-zip");
        start.ArgumentList.Add(run.ToString());
        if (File.Exists(AutomaticVJoy.DefaultPath))
        {
            var text = File.ReadAllText(AutomaticVJoy.DefaultPath).Trim();
            if (!uint.TryParse(text, out var id) || id is < 1 or > 16) { throw new InvalidDataException("The saved virtual pedal device ID is invalid. Review Settings before running setup."); }
            start.ArgumentList.Add(text);
        }
        progress.Report("Approve the Windows administrator prompt to set up the required drivers.");
        using var process = Process.Start(start) ?? throw new IOException("Could not start driver setup.");
        var deadline = Environment.TickCount64 + (long)TimeSpan.FromMinutes(16).TotalMilliseconds;
        while (!process.HasExited)
        {
            if (Environment.TickCount64 >= deadline)
            { throw new IOException("The elevated setup helper is still running. Review the setup logs before retrying."); }
            if (ReadProgress() is { } current && current.Run == run) { progress.Report(current.Message); }
            await Task.Delay(200).ConfigureAwait(false);
        }
        var result = ReadProgress();
        if (result?.Run != run) { throw new IOException("Driver setup ended without a result. Review the setup logs."); }
        if (process.ExitCode == 3010 && result.RestartRequired) { return true; }
        if (process.ExitCode != 0 || !result.Ready) { throw new IOException(result.Message); }
        return false;
    }

    internal static async Task<int> InstallAsync(Guid run, uint? selected)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) { return 1; }
        using var gate = new Semaphore(1, 1, @"Global\SherpaAxis.PrerequisiteSetup");
        if (!gate.WaitOne(0)) { return 1; }
        var status = new PrerequisiteProgress(Plan, run, "Checking bundled setup files…", Boot: Boot);
        try
        {
            if (HasActiveInstaller(ReadProgress())) { return 1; }
            Directory.CreateDirectory(LogDirectory);
            await StoreAsync(status).ConfigureAwait(false);
            VendorControl.RequireClosed();
            var setup = new PrerequisiteSetup(Inspect, () => VerifyPayload(Root), RunStep);
            var restart = await setup.InstallAsync(new ImmediateProgress(message =>
            {
                status = status with { Message = message };
                StoreAsync(status).GetAwaiter().GetResult();
            }), CancellationToken.None).ConfigureAwait(false);
            status = status with
            {
                Ready = !restart,
                RestartRequired = restart,
                ActiveProcess = null,
                ProcessStarted = null,
                Message = restart ? "Restart Windows, then open SherpaAxis again to finish device setup." : "Required drivers and virtual pedals are ready."
            };
            await StoreAsync(status).ConfigureAwait(false);
            return restart ? 3010 : 0;
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        {
            DiagnosticLog.Default.Write("prerequisite-setup", "ZIP prerequisite setup did not finish.", failure);
            await StoreAsync(status with { Message = failure.Message }).ConfigureAwait(false);
            return 1;
        }
        finally { gate.Release(); }

        async Task<int> RunStep(PrerequisiteStep step, CancellationToken token)
        {
            var start = CreateStartInfo(step, Root, LogDirectory, selected);
            using var child = Process.Start(start) ?? throw new IOException($"Could not start {step} setup.");
            status = status with { ActiveProcess = child.Id, ProcessStarted = child.StartTime.ToUniversalTime() };
            await StoreAsync(status).ConfigureAwait(false);
            string text;
            try
            {
                text = await WaitForProcessAsync(child,
                    step is PrerequisiteStep.VJoy or PrerequisiteStep.HidHide ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            { throw new IOException($"{step} setup is taking too long and may still be running. Wait for it to finish or restart Windows before retrying. Logs are in {LogDirectory}."); }
            await File.WriteAllTextAsync(Path.Combine(LogDirectory, step + ".log"), text, token).ConfigureAwait(false);
            status = status with { ActiveProcess = null, ProcessStarted = null };
            await StoreAsync(status).ConfigureAwait(false);
            return child.ExitCode;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(PrerequisiteStep step, string root, string logs, uint? selected)
    {
        var executable = step switch
        {
            PrerequisiteStep.VJoy => Path.Combine(root, "Prerequisites", VJoyInstaller),
            PrerequisiteStep.HidHide => Path.Combine(root, "Prerequisites", HidHideInstaller),
            PrerequisiteStep.DfuSe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe"),
            _ => Path.Combine(root, "SherpaAxis.App.exe")
        };
        string[] arguments = step switch
        {
            PrerequisiteStep.VJoy => ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/RESTARTEXITCODE=3010", "/COMPONENTS=Apps,Apps\\vJoyConf", "/LOG=" + Path.Combine(logs, "vjoy-install.log")],
            PrerequisiteStep.HidHide => ["/exenoui", "/qn", "/norestart"],
            PrerequisiteStep.DfuSe => ["/add-driver", Path.Combine(root, "Tools", "DfuSe", "Driver", "STtube.inf")],
            _ => selected is { } id ? ["--configure-vjoy", id.ToString(System.Globalization.CultureInfo.InvariantCulture)] : ["--configure-vjoy"]
        };
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        return start;
    }

    internal static async Task<string> WaitForProcessAsync(Process child, TimeSpan limit, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        var output = Task.WhenAll(child.StandardOutput.ReadToEndAsync(timeout.Token), child.StandardError.ReadToEndAsync(timeout.Token));
        try
        {
            await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return string.Join(Environment.NewLine, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Setup process did not finish within its deadline."); }
    }

    internal static Task StoreAsync(PrerequisiteProgress status, string? path = null) =>
        AtomicFile.WriteAsync(path ?? ProgressPath, JsonSerializer.SerializeToUtf8Bytes(status));
    private sealed class ImmediateProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
