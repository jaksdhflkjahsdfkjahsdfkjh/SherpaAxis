using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services.Firmware;

public static class WindowsFirmwareServices
{
    private static readonly IReadOnlyDictionary<string, string> ToolHashes = ReadToolHashes();
    public static FirmwareUpdateServices Create() => new(ConnectAsync, Bootloaders, CheckToolAsync, ProgramAsync, PrepareBootloader: PrepareBootloaderAsync);
    public static string FirmwareDirectory => Path.Combine(AppContext.BaseDirectory, "Firmwares");
    public static string DefaultImagePath => Path.Combine(FirmwareDirectory, "PE0017.dfu");
    public static string FindTool() => Path.Combine(AppContext.BaseDirectory, "Tools", "DfuSe", "DfuSeCommand.exe");

    private static UsbDeviceLocation[] Bootloaders()
    {
        var interfaces = WindowsUsbDevices.FindDfuInterfaces();
        var physical = WindowsUsbDevices.Find(0x0483, 0xDF11);
        return interfaces.Concat(physical).DistinctBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(device => device with { DriverReady = interfaces.Any(item => string.Equals(item.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase)) }).ToArray();
    }

    private static async Task<FirmwareConnection> ConnectAsync(CancellationToken token)
    {
        try { return await OpenControllerAsync(token).ConfigureAwait(false); }
        catch (IOException failure) when (failure.InnerException is Win32Exception { NativeErrorCode: 2 or 6 or 21 or 31 or 1167 })
        { throw new FirmwareControllerUnavailableException("The pedal USB connection is still becoming available.", failure); }
    }

    private static async Task<FirmwareConnection> OpenControllerAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureToolsClosed();
        var collections = new WindowsHidInputSource().Find(PedalInputReport.VendorId, PedalInputReport.ProductId).Where(PedalCalibrationReport.Supports).ToArray();
        var physical = WindowsUsbDevices.Find(PedalInputReport.VendorId, PedalInputReport.ProductId);
        if (collections.Length > 1 || physical.Count > 1)
        { throw new InvalidOperationException("More than one FEEL-VR pedal controller is connected. Disconnect other pedals before checking firmware."); }
        if (collections.Length != 1 || physical.Count != 1) { throw new FirmwareControllerUnavailableException("Connect exactly one FEEL-VR pedal controller in normal mode."); }
        var features = new WindowsHidFeatureConnection(collections[0]);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
            var identity = await features.ReadIdentityAsync(deadline.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(identity.SerialNumber) || !physical[0].InstanceId.EndsWith("\\" + identity.SerialNumber, StringComparison.OrdinalIgnoreCase))
            { throw new IOException("The USB port identity does not match the controller serial number."); }
            var bytes = await features.ReadAsync(collections[0], PedalCalibrationReport.ReportId, deadline.Token).ConfigureAwait(false);
            return new(new(collections[0], identity.SerialNumber, features, features, features), physical[0], PedalCalibrationReport.Decode(bytes));
        }
        catch (OperationCanceledException failure) when (!token.IsCancellationRequested)
        { await features.DisposeAsync(); throw new TimeoutException("Controller firmware preflight timed out.", failure); }
        catch { await features.DisposeAsync(); throw; }
    }

    private static async Task CheckToolAsync(string path, CancellationToken token)
    {
        EnsureToolsClosed();
        await ValidateToolFilesAsync(path, token).ConfigureAwait(false);
        var start = CreateStart(path); start.ArgumentList.Add("-?");
        using var process = Start(start);
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { try { process.Kill(true); } catch (InvalidOperationException) { } throw new IOException("DfuSe help did not respond. No controller command was sent."); }
        var text = await output.ConfigureAwait(false); _ = await error.ConfigureAwait(false);
        if (!text.Contains("DfuSe command line", StringComparison.OrdinalIgnoreCase) || !text.Contains("-d", StringComparison.Ordinal)
            || !text.Contains("--de", StringComparison.Ordinal) || !text.Contains("--al", StringComparison.Ordinal) || !text.Contains("--fn", StringComparison.Ordinal))
        { throw new IOException("This DfuSe tool does not expose the required programming options."); }
    }

    internal static async Task ValidateToolFilesAsync(string path, CancellationToken token, bool includeDriver = false)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFileName(path), "DfuSeCommand.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        { throw new IOException("The bundled DfuSe tool is missing. Run SherpaAxis Setup again."); }
        foreach (var (name, expectedHash) in ToolHashes)
        {
            if (Path.GetExtension(name) is not (".exe" or ".dll") && !(includeDriver && name.StartsWith("Driver/", StringComparison.Ordinal))) { continue; }
            var file = Path.Combine(Path.GetDirectoryName(path)!, name);
            if (!File.Exists(file)) { throw new IOException($"The bundled DfuSe file {name} is missing. Run SherpaAxis Setup again."); }
            await using var stream = File.OpenRead(file);
            if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != expectedHash)
            { throw new IOException($"The bundled DfuSe file {name} does not match the reviewed release. Run SherpaAxis Setup again."); }
        }
    }

    private static Dictionary<string, string> ReadToolHashes()
    {
        using var stream = typeof(WindowsFirmwareServices).Assembly.GetManifestResourceStream("SherpaAxis.App.DfuSeHashes.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    internal static async Task<bool> ConfigureBootloaderDriverAsync(UsbDeviceLocation device)
    {
        EnsureToolsClosed();
        await ValidateToolFilesAsync(FindTool(), CancellationToken.None, includeDriver: true).ConfigureAwait(false);
        return WindowsDfuDriver.Bind(device, Path.Combine(AppContext.BaseDirectory, "Tools", "DfuSe", "Driver", "STtube.inf"));
    }

    private static async Task PrepareBootloaderAsync(UsbDeviceLocation device)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            if (await ConfigureBootloaderDriverAsync(device).ConfigureAwait(false))
            { throw new IOException("The DfuSe driver requires a Windows restart. Keep the update record and choose Retry flash after restarting and reconnecting at the original USB port."); }
            return;
        }
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SherpaAxis.App.exe")) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in new[] { "--configure-dfuse", device.InstanceId, device.LocationPath }) { start.ArgumentList.Add(argument); }
        using var process = StartElevated(start);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        { throw new IOException(process.ExitCode == 3010 ? "The DfuSe driver requires a Windows restart. Keep the update record and choose Retry flash after restarting and reconnecting at the original USB port." : "The DfuSe USB driver could not be prepared. Review the diagnostic log and choose Retry flash after repairing the driver."); }
    }

    private static Process StartElevated(ProcessStartInfo start)
    {
        try { return Process.Start(start) ?? throw new IOException("Could not start DfuSe driver preparation."); }
        catch (Win32Exception failure) { throw new IOException("DfuSe driver preparation needs Windows administrator approval. The update remains pending.", failure); }
    }

    public static ProcessStartInfo CreateProgrammingStart(string tool, string image)
    {
        var start = CreateStart(tool);
        foreach (var arg in new[] { "-c", "--de", "0", "--al", "0", "-d", "--fn", image }) { start.ArgumentList.Add(arg); }
        return start;
    }
    private static ProcessStartInfo CreateStart(string path) => new(path)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = Path.GetDirectoryName(path)!
    };
    private static Process Start(ProcessStartInfo start)
    {
        try { return Process.Start(start) ?? throw new IOException("Could not start DfuSe."); }
        catch (Win32Exception failure) { throw new IOException("Could not start DfuSe: " + failure.Message, failure); }
    }
    private static async Task<FirmwareToolResult> ProgramAsync(string tool, string image, IProgress<string> progress)
    {
        EnsureToolsClosed();
        using var process = Start(CreateProgrammingStart(tool, image));
        var output = new StringBuilder(); var error = new StringBuilder();
        var stdout = DrainAsync(process.StandardOutput, output, process, progress);
        var stderr = DrainAsync(process.StandardError, error, process, progress);
        var completion = process.WaitForExitAsync();
        if (await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(120))).ConfigureAwait(false) != completion)
        { progress.Report("DfuSe is taking longer than expected and is still running. Keep the pedals connected; the app will wait without terminating programming."); }
        // Never kill a programming process on a timer or window cancellation.
        await completion.ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new(process.ExitCode, output.ToString(), error.ToString(), ReadbackRequested: false);
    }

    private static void EnsureToolsClosed()
    {
        Diagnostics.VendorControl.RequireClosed();
        foreach (var name in new[] { "DfuSeCommand", "DfuSeDemo" })
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes) { process.Dispose(); }
            if (processes.Length != 0) { throw new IOException("Close FEEL-VR Control and other DfuSe tools before preparing or programming firmware."); }
        }
    }
    private static async Task DrainAsync(StreamReader reader, StringBuilder buffer, Process process, IProgress<string> progress)
    {
        var chunk = new char[1024];
        var recent = "";
        while (await reader.ReadAsync(chunk).ConfigureAwait(false) is var length && length > 0)
        {
            var text = new string(chunk, 0, length); buffer.Append(text);
            if (buffer.Length > 60000) { buffer.Remove(0, buffer.Length - 60000); }
            recent += text;
            if (recent.Contains("Press any key to continue", StringComparison.OrdinalIgnoreCase))
            {
                try { await process.StandardInput.WriteLineAsync().ConfigureAwait(false); await process.StandardInput.FlushAsync().ConfigureAwait(false); }
                catch (Exception failure) when (failure is IOException or InvalidOperationException) { }
                recent = "";
            }
            if (recent.Length > 2048) { recent = recent[^2048..]; }
            progress.Report("DfuSe is programming firmware… Keep the pedals connected.");
        }
    }
}
