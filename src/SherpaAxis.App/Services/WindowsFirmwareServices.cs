using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services;

public static class WindowsFirmwareServices
{
    public static FirmwareUpdateServices Create() => new(ConnectAsync, Bootloaders, CheckToolAsync, ProgramAsync);
    public static string DefaultImagePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "feel-vr.com", "FEEL-VR Control", "Firmwares", "PE0017.dfu");
    public static string FindTool()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "STMicroelectronics", "Software");
        try { return Directory.EnumerateDirectories(root).OrderDescending(StringComparer.OrdinalIgnoreCase).Select(path => Path.Combine(path, "Bin", "DfuSeCommand.exe")).FirstOrDefault(File.Exists) ?? ""; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static UsbDeviceLocation[] Bootloaders()
    {
        var interfaces = WindowsUsbDevices.FindDfuInterfaces();
        var physical = WindowsUsbDevices.Find(0x0483, 0xDF11);
        return interfaces.Concat(physical).DistinctBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(device => device with { DriverReady = interfaces.Any(item => string.Equals(item.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase)) }).ToArray();
    }

    private static async Task<FirmwareConnection> ConnectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureToolsClosed();
        var collections = new WindowsHidInputSource().Find(PedalInputReport.VendorId, PedalInputReport.ProductId).Where(PedalCalibrationReport.Supports).ToArray();
        var physical = WindowsUsbDevices.Find(PedalInputReport.VendorId, PedalInputReport.ProductId);
        if (collections.Length != 1 || physical.Count != 1) { throw new IOException("Connect exactly one FEEL-VR pedal controller in normal mode."); }
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
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFileName(path), "DfuSeCommand.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        { throw new IOException("Select the installed ST DfuSeCommand.exe before preparing the update."); }
        var version = FileVersionInfo.GetVersionInfo(path);
        if (version.CompanyName?.Contains("STMicroelectronics", StringComparison.OrdinalIgnoreCase) != true)
        { throw new IOException("The selected program is not identified as the STMicroelectronics DfuSe command tool."); }
        foreach (var name in new[] { "STDFU.dll", "STDFUPRT.dll", "STDFUFiles.dll" })
        { if (!File.Exists(Path.Combine(Path.GetDirectoryName(path)!, name))) { throw new IOException("The DfuSe installation is incomplete. Install the complete ST package, including its USB driver."); } }
        var start = CreateStart(path); start.ArgumentList.Add("-?");
        using var process = Start(start);
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { try { process.Kill(true); } catch (InvalidOperationException) { } throw new IOException("DfuSe help did not respond. No controller command was sent."); }
        var text = await output.ConfigureAwait(false); _ = await error.ConfigureAwait(false);
        if (!text.Contains("DfuSe command line", StringComparison.OrdinalIgnoreCase) || !text.Contains("--v", StringComparison.Ordinal)
            || !text.Contains("--de", StringComparison.Ordinal) || !text.Contains("--fn", StringComparison.Ordinal))
        { throw new IOException("This DfuSe tool does not expose the required programming and verification options."); }
    }

    public static ProcessStartInfo CreateProgrammingStart(string tool, string image)
    {
        var start = CreateStart(tool);
        foreach (var arg in new[] { "-c", "--de", "0", "--al", "0", "-d", "--v", "--fn", image }) { start.ArgumentList.Add(arg); }
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
        return new(process.ExitCode, output.ToString(), error.ToString());
    }

    private static void EnsureToolsClosed()
    {
        foreach (var name in new[] { "FEEL_VR_Control", "DfuSeCommand", "DfuSeDemo" })
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
            progress.Report("DfuSe is programming or verifying… Keep the pedals connected.");
        }
    }
}
