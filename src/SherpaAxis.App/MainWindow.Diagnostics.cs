using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Output;
using SherpaAxis.Bridge;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private Task? diagnosticsCheck;
    private int diagnosedConnection;
    private bool diagnosedReceiving;
    private long vendorControlCheckedAt;

    internal void RefreshVendorControlStatus()
    {
        vendorControlCheckedAt = Environment.TickCount64;
        if (closing || desktop.VendorControlRunning is null) { return; }
        try
        {
            var running = desktop.VendorControlRunning();
            if (running != viewModel.VendorControlRunning)
            {
                viewModel.VendorControlRunning = running;
                desktop.Log?.Write("vendor-control", running ? VendorControl.ConflictMessage : "FEEL-VR Control is closed.");
                if (!viewModel.IsCalibrationMode) { _ = CheckDeviceDiagnosticsAsync(); }
            }
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("vendor-control", "Could not check whether FEEL-VR Control is running.", failure); }
    }
    internal Task CheckDeviceDiagnosticsAsync()
    {
        if (closing || desktop.Diagnostics is null) { return Task.CompletedTask; }
        return diagnosticsCheck is { IsCompleted: false } ? diagnosticsCheck : diagnosticsCheck = CheckDeviceDiagnosticsCoreAsync();
    }
    private async Task CheckDeviceDiagnosticsCoreAsync()
    {
        viewModel.Diagnostics.Busy = true;
        viewModel.Diagnostics.Status = "Checking device and virtual output…";
        try
        {
            var result = await Task.Run(() => desktop.Diagnostics!.CheckAsync(lifetime.Token, () => engine.OwnedControllerIdentity), lifetime.Token);
            if (closing) { return; }
            viewModel.Diagnostics.Apply(result);
            foreach (var check in result.Checks.Where(check => check.NeedsAttention)) { desktop.Log?.Write("device-check", check.Title + ": " + check.Detail); }
            if (viewModel.Recovery.IsBlocked)
            {
                viewModel.Diagnostics.Checks.Add(new("Calibration recovery", "Output is paused until interrupted calibration is resolved.", true));
                viewModel.Diagnostics.Status = "Setup needs attention. See the checks above.";
            }
            else { viewModel.Diagnostics.Checks.Add(new("Calibration recovery", "No interrupted calibration is blocking output.")); }
            if (viewModel.Firmware.Blocked)
            {
                viewModel.Diagnostics.Checks.Add(new("Firmware update", "A firmware update needs review. Virtual output remains paused.", true));
                viewModel.Diagnostics.Status = "Setup needs attention. See the checks above.";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("diagnostics", "An operation needs attention.", failure); if (!closing) { viewModel.Diagnostics.Status = $"Device checks failed: {failure.Message}"; } }
        finally { viewModel.Diagnostics.Busy = false; }
    }
    private async void CheckDiagnosticsClick(object sender, RoutedEventArgs e) { await UpdateInputVisibilityAsync(); await CheckDeviceDiagnosticsAsync(); }
    private void ControllerTestClick(object sender, RoutedEventArgs e) => OpenDiagnosticTool("control.exe", "joy.cpl");
    private void VJoyConfigurationClick(object sender, RoutedEventArgs e) => OpenDiagnosticTool(
        Path.Combine(Path.GetDirectoryName(VJoyController.DefaultLibraryPath)!, "vJoyConf.exe"));
    private void SetupGuideClick(object sender, RoutedEventArgs e) => CreateSetupGuideWindow().ShowDialog();
    internal Window CreateSetupGuideWindow()
    {
        var dialog = new Window
        {
            Owner = this,
            Width = 620,
            Height = 510,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Foreground = Foreground
        };
        Localization.Localizer.Bind(dialog, Window.TitleProperty, "Device setup guide");
        var text = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), FontSize = 14 };
        Localization.Localizer.Bind(text, System.Windows.Controls.TextBlock.TextProperty,
            "Install SherpaAxis with its installer to install vJoy and its SDK together. Complete any Windows restart requested by the driver installer.\n\nAutomatic setup creates a free vJoy device with X, Y and Z axes. Existing configurations are preserved. SherpaAxis remembers the selected ID so game bindings stay stable. If that device is busy later, close its other feeder.\n\nIn your game, bind X to throttle, Y to brake and Z to clutch from the selected vJoy device. SherpaAxis must stay running to send processed pedal input; minimizing to the tray keeps output running, while Exit stops it. HidHide prevents duplicate physical-pedal input while SherpaAxis runs. Keep the physical HID driver installed. Use Settings to turn physical-pedal hiding off. Close SherpaAxis before using the physical pedals directly.\n\nNative calibration supports PE0 firmware 017. Setup includes firmware 017, ST DfuSe and its USB driver. In Settings → Firmware update, choose Update firmware, select the bundled image and choose Flash. After programming and verification, reconnect the pedals and choose Check firmware. Open update logs shows retained logs and automatic calibration backups. Backup calibration saves a separate settings backup. Newer firmware is not downgraded.\n\nResolve any interrupted-calibration warning before using virtual output. Check again after setup or reconnecting the pedals.");
        dialog.Content = new System.Windows.Controls.ScrollViewer { Content = text, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
        dialog.SourceInitialized += (_, _) => WindowTheme.Apply(dialog);
        return dialog;
    }
    private void OpenDiagnosticTool(string path, string? argument = null)
    {
        try { Process.Start(new ProcessStartInfo(path, argument ?? "") { UseShellExecute = true }); }
        catch (Exception failure) when (failure is Win32Exception or IOException or InvalidOperationException)
        { viewModel.Diagnostics.Status = $"Could not open setup tool: {failure.Message}"; }
    }
    private async void AutomaticSetupClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (hardwareSession is not null || WantsCalibration || viewModel.Recovery.IsBlocked)
        { viewModel.Diagnostics.Status = "Finish calibration recovery before configuring virtual output."; return; }
        await (runtime?.PauseAsync() ?? Task.CompletedTask);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SherpaAxis.App.exe")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--configure-vjoy");
            var rememberedPath = AutomaticVJoy.DefaultPath;
            if (File.Exists(rememberedPath))
            {
                var saved = File.ReadAllText(rememberedPath).Trim();
                if (!uint.TryParse(saved, out var id) || id is < 1 or > 16) { throw new InvalidDataException("The saved vJoy device ID is invalid."); }
                start.ArgumentList.Add(saved);
            }
            using var process = Process.Start(start) ?? throw new IOException("Could not start automatic setup.");
            await process.WaitForExitAsync(lifetime.Token);
            viewModel.ShowMessage(process.ExitCode == 0 ? "Virtual pedal device is ready." : "Automatic setup needs attention. Check Settings; restart Windows if requested.");
        }
        catch (Win32Exception failure) { viewModel.ShowMessage($"Automatic setup did not start: {failure.Message}"); }
        finally { if (!closing) { await ResumeLiveAfterRecoveryAsync(); await CheckDeviceDiagnosticsAsync(); } }
    });
    private async void ExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Localization.Localizer.Translate("Export diagnostic report"),
            Filter = "ZIP (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = "SherpaAxis-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".zip"
        };
        if (dialog.ShowDialog(this) == true) { await RunAsync(() => ExportDiagnosticsAsync(dialog.FileName)); }
    }
    internal Task ExportDiagnosticsAsync(string path)
    {
        var status = new Dictionary<string, string>
        {
            ["input"] = viewModel.Status,
            ["output"] = viewModel.OutputInfo,
            ["calibrationRecoveryBlocked"] = viewModel.Recovery.IsBlocked.ToString(),
            ["firmwareReviewBlocked"] = viewModel.Firmware.Blocked.ToString(),
            ["firmwareOperationRunning"] = viewModel.Firmware.Running.ToString(),
            ["language"] = viewModel.Preferences.Language
        };
        foreach (var check in viewModel.Diagnostics.Checks) { status[check.Title] = check.Detail; }
        return ExportCoreAsync();
        async Task ExportCoreAsync()
        {
            if (desktop.Log is null) { throw new InvalidOperationException("Local diagnostic logging is unavailable."); }
            await Task.Run(() => new DiagnosticReportExporter(desktop.Log).Export(path, status));
            viewModel.ShowMessage("Diagnostic report exported. Review the ZIP before sharing it.");
        }
    }
}
