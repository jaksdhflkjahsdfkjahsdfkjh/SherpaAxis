using System.IO;
using System.Windows;
using Microsoft.Win32;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Firmware;
using SherpaAxis.App.Services.Hardware;

namespace SherpaAxis.App;

public partial class MainWindow
{
    internal async Task CheckFirmwareStartupAsync()
    {
        if (desktop.Firmware is null || closing) { return; }
        try
        {
            var pending = await desktop.Firmware.ReadPendingAsync(lifetime.Token);
            viewModel.Firmware.Blocked = pending is not null;
            if (pending is not null) { viewModel.Firmware.Status = "A firmware update needs review. Output remains paused. Reconnect the pedals and choose Check firmware, or open Update firmware to retry at the recorded USB port."; }
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.Blocked = true; viewModel.Firmware.Status = $"Firmware update needs review: {failure.Message}"; }
    }

    private async void OpenFirmwareUpdateClick(object sender, RoutedEventArgs e)
    {
        if (desktop.Firmware is null || viewModel.Firmware.Running) { return; }
        try
        {
            var model = viewModel.Firmware;
            model.Completed = false;
            var pending = await desktop.Firmware.ReadPendingAsync(lifetime.Token);
            model.Blocked = pending is not null;
            if (pending is not null)
            {
                model.Images = [new(pending.StagedImagePath, pending.Plan.Image.Version, Path.GetFileName(pending.Plan.ImagePath))];
                model.SelectedImage = model.Images[0];
            }
            else
            {
                var catalog = FirmwareCatalog.Scan(WindowsFirmwareServices.FirmwareDirectory);
                var previous = model.SelectedImage?.Path;
                model.Images = catalog.Images;
                model.SelectedImage = catalog.Images.FirstOrDefault(image => image.Path == previous) ?? (catalog.Images.Count == 0 ? null : catalog.Images[0]);
                model.Status = catalog.Images.Count == 0 ? "No supported firmware was found in the Firmwares folder. Run SherpaAxis Setup again." : "Ready to update the connected pedal controller.";
                if (catalog.RejectedFiles.Count != 0) { model.Status += "\n" + Localizer.Translate("Unsupported or unreadable firmware files were skipped:") + " " + string.Join(", ", catalog.RejectedFiles); }
            }
            if (!closing)
            {
                new FirmwareUpdateWindow(model, Resources, () => UpdateFirmwareAsync(model.SelectedImage?.Path, model.Blocked),
                CheckFirmwareAsync, RestoreFirmwareCalibrationWithReviewAsync, () => CheckUpdatedFirmwareAsync(true))
                { Owner = this }.ShowDialog();
            }
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.Status = failure.Message; }
    }

    private void OpenFirmwareLogClick(object sender, RoutedEventArgs e)
    {
        if (desktop.Firmware is null) { return; }
        try { Directory.CreateDirectory(desktop.Firmware.DirectoryPath); OpenDiagnosticTool(desktop.Firmware.DirectoryPath); }
        catch (Exception failure) when (OperationErrors.IsExpected(failure)) { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.Status = failure.Message; }
    }

    internal Task UpdateFirmwareAsync(string? imagePath, bool retry = false) => RunAsync(async () =>
    {
        if (desktop.Firmware is null || hardwareSession is not null || viewModel.Recovery.IsBlocked)
        { viewModel.Firmware.Status = "Finish hardware calibration and recovery before updating firmware."; return; }
        viewModel.Firmware.Running = true;
        viewModel.Firmware.Completed = false;
        viewModel.Firmware.CanAccept = false;
        var progress = new Progress<string>(message => { if (viewModel.Firmware.Running && !viewModel.Firmware.Completed) { viewModel.Firmware.Status = message; } });
        try
        {
            await PauseForFirmwareAsync();
            if (retry) { await desktop.Firmware.RetryAsync(progress, lifetime.Token); }
            else
            {
                if (string.IsNullOrWhiteSpace(imagePath)) { throw new IOException("Select a supported firmware before flashing."); }
                viewModel.Firmware.Status = "Checking firmware, tool, controller identity and USB port…";
                var plan = await Task.Run(() => desktop.Firmware.PrepareAsync(imagePath, WindowsFirmwareServices.FindTool(), lifetime.Token));
                await desktop.Firmware.FlashAsync(plan, progress, lifetime.Token);
            }
            await desktop.Firmware.CompleteUpdateAsync(progress, lifetime.Token);
            await CheckStartupRecoveryAsync();
            if (viewModel.Recovery.IsBlocked) { throw new IOException("Controller recovery needs attention before output can resume."); }
            viewModel.Firmware.Completed = true;
            viewModel.Firmware.Status = "Firmware update complete. Firmware 017 and original calibration checked. Pedals are ready.";
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.Status = $"Firmware update needs review: {failure.Message}"; }
        finally
        {
            var detail = viewModel.Firmware.Status;
            await CheckFirmwareStartupAsync();
            viewModel.Firmware.Status = detail;
            viewModel.Firmware.Running = false;
            await ResumeLiveAfterRecoveryAsync();
        }
    });

    private async void CheckFirmwareClick(object sender, RoutedEventArgs e) => await CheckFirmwareAsync();

    internal async Task CheckFirmwareAsync()
    {
        if (viewModel.Firmware.Blocked) { await CheckUpdatedFirmwareAsync(false); }
        else { await InspectFirmwareAsync(); }
    }

    private async Task PauseForFirmwareAsync()
    {
        await (runtime?.PauseAsync() ?? Task.CompletedTask);
        await CheckStartupRecoveryAsync();
        if (viewModel.Recovery.IsBlocked)
        { throw new IOException("Restore the original hardware calibration before using firmware tools. Temporary raw-input bounds must not be backed up or flashed."); }
    }

    private async Task RestoreFirmwareCalibrationWithReviewAsync()
    {
        if (desktop.Firmware is null || viewModel.Firmware.Running) { return; }
        try
        {
            var pending = await desktop.Firmware.ReadPendingAsync(lifetime.Token);
            if (pending is null) { return; }
            var bounds = pending.Plan.Calibration;
            var message = Localizer.Translate("Restore and save the original calibration from before this firmware update? Keep your feet off all pedals. The current controller calibration will be backed up first.")
                + $"\n\nThrottle: {bounds.Throttle.Minimum} → {bounds.Throttle.Maximum}\nBrake: {bounds.Brake.Minimum} → {bounds.Brake.Maximum}\nClutch: {bounds.Clutch.Minimum} → {bounds.Clutch.Maximum}";
            if (MessageBox.Show(OwnedWindows.OfType<FirmwareUpdateWindow>().FirstOrDefault() ?? (Window)this, message,
                Localizer.Translate("Restore calibration backup"), MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) { return; }
            await RestoreFirmwareCalibrationAsync();
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("firmware", "Calibration backup needs attention.", failure); viewModel.Firmware.Status = failure.Message; }
    }

    internal Task RestoreFirmwareCalibrationAsync() => RunAsync(async () =>
    {
        if (desktop.Firmware is null || hardwareSession is not null || viewModel.Recovery.IsBlocked) { return; }
        viewModel.Firmware.Running = true;
        try
        {
            await PauseForFirmwareAsync();
            viewModel.Firmware.Status = await Task.Run(() => desktop.Firmware.RestoreCalibrationBackupAsync(lifetime.Token));
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("firmware", "Calibration restoration needs attention.", failure); viewModel.Firmware.Status = failure.Message; }
        finally { viewModel.Firmware.Running = false; }
    });

    internal Task InspectFirmwareAsync() => RunAsync(async () =>
    {
        if (desktop.Firmware is null || hardwareSession is not null || viewModel.Recovery.IsBlocked) { return; }
        viewModel.Firmware.Running = true;
        try
        {
            await PauseForFirmwareAsync();
            viewModel.Firmware.Status = await Task.Run(() => desktop.Firmware.CheckFirmwareAsync(lifetime.Token));
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.Status = $"Could not check controller firmware: {failure.Message}"; }
        finally { viewModel.Firmware.Running = false; await ResumeLiveAfterRecoveryAsync(); }
    });

    private async void BackupFirmwareCalibrationClick(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Title = Localizer.Translate("Backup current hardware settings"),
            Filter = Localizer.Translate("SherpaAxis hardware backup (*.json)|*.json"),
            DefaultExt = ".json",
            AddExtension = true,
            FileName = $"pedals-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (picker.ShowDialog(this) == true) { await BackupFirmwareCalibrationAsync(picker.FileName); }
    }

    internal Task BackupFirmwareCalibrationAsync(string path) => RunAsync(async () =>
    {
        if (desktop.Firmware is null || hardwareSession is not null || viewModel.Recovery.IsBlocked || viewModel.Firmware.Blocked) { return; }
        viewModel.Firmware.Running = true;
        try
        {
            await PauseForFirmwareAsync();
            var backup = await Task.Run(() => desktop.Firmware.BackupCalibrationAsync(lifetime.Token));
            await new HardwareRecoveryStore(path).WriteAsync(backup, lifetime.Token);
            viewModel.Firmware.Status = $"Hardware backup saved: {path}. Controller settings were not changed.";
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.Status = failure.Message; }
        finally { viewModel.Firmware.Running = false; await ResumeLiveAfterRecoveryAsync(); }
    });

    internal Task CheckUpdatedFirmwareAsync(bool accept) => RunAsync(async () =>
    {
        if (desktop.Firmware is null || hardwareSession is not null) { return; }
        viewModel.Firmware.Running = true;
        viewModel.Firmware.Completed = false;
        try
        {
            await PauseForFirmwareAsync();
            var pending = await desktop.Firmware.ReadPendingAsync(lifetime.Token);
            var progress = new Progress<string>(message => { if (viewModel.Firmware.Running && !viewModel.Firmware.Completed) { viewModel.Firmware.Status = message; } });
            var result = await Task.Run(() => !accept && pending is { } record && (record.DownloadCompleted || record.ProgramVerified)
                ? desktop.Firmware.CompleteUpdateAsync(progress, lifetime.Token)
                : desktop.Firmware.CheckConnectionAsync(accept, lifetime.Token));
            viewModel.Firmware.Blocked = !result.Complete; viewModel.Firmware.CanAccept = result.CanAccept;
            viewModel.Firmware.Status = result.Detail;
            viewModel.Firmware.Completed = result.Complete;
            if (result.Complete) { await CheckStartupRecoveryAsync(); await CheckDeviceDiagnosticsAsync(); }
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.CanAccept = false; viewModel.Firmware.Status = $"Firmware update needs review: {failure.Message}"; }
        finally { viewModel.Firmware.Running = false; await ResumeLiveAfterRecoveryAsync(); }
    });
}
