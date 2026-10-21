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
            if (!closing) { new FirmwareUpdateWindow(model, Resources, () => UpdateFirmwareAsync(model.SelectedImage?.Path, model.Blocked)) { Owner = this }.ShowDialog(); }
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
        viewModel.Firmware.CanAccept = false;
        var progress = new Progress<string>(message => viewModel.Firmware.Status = message);
        try
        {
            await (runtime?.PauseAsync() ?? Task.CompletedTask);
            if (retry) { await desktop.Firmware.RetryAsync(progress, lifetime.Token); }
            else
            {
                if (string.IsNullOrWhiteSpace(imagePath)) { throw new IOException("Select a supported firmware before flashing."); }
                viewModel.Firmware.Status = "Checking firmware, tool, controller identity and USB port…";
                var plan = await Task.Run(() => desktop.Firmware.PrepareAsync(imagePath, WindowsFirmwareServices.FindTool(), lifetime.Token));
                await desktop.Firmware.FlashAsync(plan, progress, lifetime.Token);
            }
            viewModel.Firmware.Status = "Programming verified. Unplug and reconnect the pedals, then choose Check firmware.";
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

    private async void CheckFirmwareClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.Firmware.Blocked)
        {
            await CheckUpdatedFirmwareAsync(false);
            if (viewModel.Firmware.CanAccept && MessageBox.Show(this,
                Localizer.Translate(viewModel.Firmware.Status) + "\n\n" + Localizer.Translate("Accept the detected firmware and current hardware calibration? This releases virtual output. An interrupted programming result is not a verified flash; retained backups and logs remain available."),
                Localizer.Translate("Accept detected controller"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK)
            { await CheckUpdatedFirmwareAsync(true); }
        }
        else { await InspectFirmwareAsync(); }
    }

    internal Task InspectFirmwareAsync() => RunAsync(async () =>
    {
        if (desktop.Firmware is null || hardwareSession is not null || viewModel.Recovery.IsBlocked) { return; }
        viewModel.Firmware.Running = true;
        try
        {
            await (runtime?.PauseAsync() ?? Task.CompletedTask);
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
            await (runtime?.PauseAsync() ?? Task.CompletedTask);
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
        try
        {
            await (runtime?.PauseAsync() ?? Task.CompletedTask);
            var result = await Task.Run(() => desktop.Firmware.CheckConnectionAsync(accept, lifetime.Token));
            viewModel.Firmware.Blocked = !result.Complete; viewModel.Firmware.CanAccept = result.CanAccept;
            viewModel.Firmware.Status = result.Detail;
            if (result.Complete) { await CheckStartupRecoveryAsync(); await CheckDeviceDiagnosticsAsync(); }
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.CanAccept = false; viewModel.Firmware.Status = $"Firmware update needs review: {failure.Message}"; }
        finally { viewModel.Firmware.Running = false; await ResumeLiveAfterRecoveryAsync(); }
    });
}
