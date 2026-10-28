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
    private bool diagnosticsAgain;
    private bool diagnosedRecoveryBlocked, diagnosedFirmwareBlocked;
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
        if (diagnosticsCheck is { IsCompleted: false })
        {
            diagnosticsAgain = true;
            return diagnosticsCheck;
        }
        return diagnosticsCheck = RefreshDeviceDiagnosticsAsync();
    }
    internal Task EnsureDeviceDiagnosticsAsync()
    {
        if (diagnosticsCheck is null || diagnosticsCheck.IsCompleted &&
            (diagnosedRecoveryBlocked != viewModel.Recovery.IsBlocked || diagnosedFirmwareBlocked != viewModel.Firmware.Blocked))
        { return CheckDeviceDiagnosticsAsync(); }
        return diagnosticsCheck;
    }
    private async Task RefreshDeviceDiagnosticsAsync()
    {
        viewModel.Diagnostics.Busy = true;
        viewModel.Diagnostics.Status = "Checking device and virtual output…";
        try
        {
            do
            {
                diagnosticsAgain = false;
                await CheckDeviceDiagnosticsCoreAsync();
            } while (diagnosticsAgain && !closing);
        }
        finally { viewModel.Diagnostics.Busy = false; }
    }
    private async Task CheckDeviceDiagnosticsCoreAsync()
    {
        try
        {
            var result = await Task.Run(() => desktop.Diagnostics!.CheckAsync(lifetime.Token, () => engine.OwnedControllerIdentity), lifetime.Token);
            if (closing || diagnosticsAgain) { return; }
            diagnosedRecoveryBlocked = viewModel.Recovery.IsBlocked;
            diagnosedFirmwareBlocked = viewModel.Firmware.Blocked;
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
    }
    private async void CheckDiagnosticsClick(object sender, RoutedEventArgs e) { await UpdateInputVisibilityAsync(); await CheckDeviceDiagnosticsAsync(); }
    private void ControllerTestClick(object sender, RoutedEventArgs e) => OpenDiagnosticTool("control.exe", "joy.cpl");
    private void VJoyConfigurationClick(object sender, RoutedEventArgs e) => OpenDiagnosticTool(
        Path.Combine(Path.GetDirectoryName(VJoyController.DefaultLibraryPath)!, "vJoyConf.exe"));
    private void SetupGuideClick(object sender, RoutedEventArgs e) => CreateSetupGuideWindow().ShowDialog();
    private void ReportBugNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        OpenDiagnosticTool(ViewModels.DashboardViewModel.IssuesUri.AbsoluteUri);
    }
    internal Window CreateSetupGuideWindow()
    {
        var dialog = new Window
        {
            Owner = this,
            Width = Math.Min(740, SystemParameters.WorkArea.Width),
            Height = Math.Min(650, SystemParameters.WorkArea.Height),
            MinWidth = Math.Min(480, SystemParameters.WorkArea.Width),
            MinHeight = Math.Min(360, SystemParameters.WorkArea.Height),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Foreground = Foreground,
            FontFamily = FontFamily
        };
        dialog.Resources.MergedDictionaries.Add(Resources);
        Localization.Localizer.Bind(dialog, Window.TitleProperty, "Device setup guide");
        var document = new System.Windows.Documents.FlowDocument
        {
            PagePadding = new Thickness(24),
            FontFamily = FontFamily,
            FontSize = 14,
            Foreground = Foreground,
            Background = Background,
            ColumnWidth = double.PositiveInfinity
        };
        AddGuideSection(document, "Close FEEL-VR Control",
            "Exit FEEL-VR Control completely, including its tray instance, before using SherpaAxis. Keep games closed when calibrating or changing device setup.");
        AddGuideSection(document, "Game bindings", "X = throttle · Y = brake · Z = clutch", emphasizeBody: true);
        AddGuideSection(document, "Virtual output and HidHide",
            "SherpaAxis must stay running to send processed pedal input. Minimizing to the tray keeps output running; Exit stops it. HidHide hides only the physical pedal joystick from games. Keep the physical HID driver installed. Turn physical-pedal hiding off in Settings when needed.");
        AddGuideSection(document, "Device setup",
            "Automatic setup creates a free vJoy device with X, Y and Z axes. Existing configurations are preserved. SherpaAxis remembers the selected ID so game bindings stay stable. If that device is busy later, close its other feeder.");
        AddGuideSection(document, "Firmware updates",
            "Native calibration supports PE0 firmware 017. Setup includes firmware 017, ST DfuSe and its USB driver. In Settings → Firmware update, choose Update firmware, select the bundled image and choose Flash. Leave the pedals connected. SherpaAxis waits for reconnection, restores and saves the original calibration, and checks firmware and calibration automatically. Click OK when the update finishes. Byte readback is not performed. Open update logs shows retained logs and automatic calibration backups. Backup calibration saves a separate settings backup. Newer firmware is not downgraded.");
        AddGuideSection(document, "Pedal drift correction",
            "Calibrate once in SherpaAxis to retain the original raw range. Auto waits for five consistent press/release cycles. Hold Fix drift with your foot off for at least one stable second, and keep holding until correction finishes. Release to stop. By button uses the same stability check. Save corrected calibration is a separate controller write.");
        AddGuideSection(document, "Recovery",
            "Resolve any interrupted-calibration warning before using virtual output. Check again after setup or reconnecting the pedals.");
        dialog.Content = new System.Windows.Controls.FlowDocumentScrollViewer
        {
            Document = document,
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
        };
        dialog.SourceInitialized += (_, _) => WindowTheme.Apply(dialog);
        return dialog;
    }
    private static void AddGuideSection(System.Windows.Documents.FlowDocument document, string title, string body, bool emphasizeBody = false)
    {
        var heading = new System.Windows.Documents.Run();
        Localization.Localizer.Bind(heading, System.Windows.Documents.Run.TextProperty, title);
        document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Bold(heading))
        {
            FontSize = 16,
            Margin = new Thickness(0, document.Blocks.Count == 0 ? 0 : 16, 0, 6),
            Foreground = new System.Windows.Media.SolidColorBrush(title == "Close FEEL-VR Control"
                ? System.Windows.Media.Color.FromRgb(255, 206, 131) : System.Windows.Media.Color.FromRgb(120, 227, 198))
        });
        var text = new System.Windows.Documents.Run();
        Localization.Localizer.Bind(text, System.Windows.Documents.Run.TextProperty, body);
        System.Windows.Documents.Inline inline = emphasizeBody ? new System.Windows.Documents.Bold(text) : text;
        document.Blocks.Add(new System.Windows.Documents.Paragraph(inline) { Margin = new Thickness(0), LineHeight = 24 });
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
        if (ShowTextEntryDialog(() => dialog.ShowDialog(this)) == true) { await RunAsync(() => ExportDiagnosticsAsync(dialog.FileName)); }
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
