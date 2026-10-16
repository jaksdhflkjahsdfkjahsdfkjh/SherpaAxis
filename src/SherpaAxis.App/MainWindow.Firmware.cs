using System.IO;
using System.Windows;
using Microsoft.Win32;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;

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
            if (pending is not null) { viewModel.Firmware.Status = "A firmware update needs review. Output remains paused. Reconnect the pedals and check the updated controller, or retry programming at the recorded USB port."; }
        }
        catch (Exception failure) when (IsExpected(failure))
        { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.Blocked = true; viewModel.Firmware.Status = $"Firmware update needs review: {failure.Message}"; }
    }
    private void SelectFirmwareImageClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = Localizer.Translate("Select FEEL-VR firmware"), Filter = "FEEL-VR Pedals One (*.dfu)|*.dfu", CheckFileExists = true };
        var folder = Path.GetDirectoryName(WindowsFirmwareServices.DefaultImagePath)!;
        if (Directory.Exists(folder)) { picker.InitialDirectory = folder; }
        if (picker.ShowDialog(this) == true) { viewModel.Firmware.ImagePath = picker.FileName; }
    }
    private void SelectDfuToolClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = Localizer.Translate("Select ST DfuSeCommand.exe"), Filter = "DfuSeCommand.exe|DfuSeCommand.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) { viewModel.Firmware.ToolPath = picker.FileName; }
    }
    private void OpenDfuSetupClick(object sender, RoutedEventArgs e) => OpenDiagnosticTool("https://www.st.com/en/development-tools/stsw-stm32080.html");
    private void OpenFirmwareLogClick(object sender, RoutedEventArgs e)
    {
        if (desktop.Firmware is null) { return; }
        try { Directory.CreateDirectory(desktop.Firmware.DirectoryPath); OpenDiagnosticTool(desktop.Firmware.DirectoryPath); }
        catch (Exception failure) when (IsExpected(failure)) { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.Status = failure.Message; }
    }
    private async void PrepareFirmwareClick(object sender, RoutedEventArgs e) => await PrepareFirmwareAsync();
    internal Task PrepareFirmwareAsync() => RunAsync(async () =>
    {
        if (desktop.Firmware is null) { return; }
        if (hardwareSession is not null || viewModel.Recovery.IsBlocked)
        { viewModel.Firmware.Status = "Finish hardware calibration and recovery before preparing firmware."; return; }
        viewModel.Firmware.Plan = null;
        viewModel.Firmware.Running = true;
        try
        {
            await (runtime?.PauseAsync() ?? Task.CompletedTask);
            if (string.IsNullOrWhiteSpace(viewModel.Firmware.ImagePath)) { viewModel.Firmware.ImagePath = WindowsFirmwareServices.DefaultImagePath; }
            if (string.IsNullOrWhiteSpace(viewModel.Firmware.ToolPath)) { viewModel.Firmware.ToolPath = WindowsFirmwareServices.FindTool(); }
            viewModel.Firmware.Status = "Checking firmware, tool, controller identity and USB port…";
            viewModel.Firmware.Plan = await Task.Run(() => desktop.Firmware.PrepareAsync(viewModel.Firmware.ImagePath, viewModel.Firmware.ToolPath, lifetime.Token));
            viewModel.Firmware.Status = "Ready to flash the prepared controller. Close FEEL-VR Control and games. Keep the USB cable connected during programming.";
        }
        catch (Exception failure) when (IsExpected(failure)) { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.Status = failure.Message; }
        finally { viewModel.Firmware.Running = false; await ResumeLiveAfterRecoveryAsync(); }
    });
    private async void FlashFirmwareClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.Firmware.Plan is not { } plan) { return; }
        var message = Localizer.Translate("Program this controller with firmware 017? Close FEEL-VR Control and games first. A hardware-settings backup will be saved. Keep the pedals powered and connected until verification finishes.")
            + "\n\n" + Localizer.Translate(viewModel.Firmware.PlanInfo);
        if (MessageBox.Show(this, message, Localizer.Translate("Flash pedal firmware"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) { return; }
        await RunFirmwareProgrammingAsync(plan, false);
    }
    private async void RetryFirmwareClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Localizer.Translate("Retry the reviewed image on the original pedal bootloader at its recorded USB port? Disconnect other DFU devices and keep the cable connected."), Localizer.Translate("Retry firmware programming"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK)
        { await RunFirmwareProgrammingAsync(null, true); }
    }
    internal Task RunFirmwareProgrammingAsync(FirmwarePlan? plan, bool retry) => RunAsync(async () =>
    {
        if (desktop.Firmware is null || hardwareSession is not null || viewModel.Recovery.IsBlocked) { return; }
        viewModel.Firmware.Running = true;
        viewModel.Firmware.CanAccept = false;
        var progress = new Progress<string>(message => viewModel.Firmware.Status = message);
        try
        {
            await (runtime?.PauseAsync() ?? Task.CompletedTask);
            if (retry) { await desktop.Firmware.RetryAsync(progress, lifetime.Token); }
            else { await desktop.Firmware.FlashAsync(plan ?? throw new InvalidOperationException("Prepare the firmware first."), progress, lifetime.Token); }
            viewModel.Firmware.Status = "Programming verified. Unplug and reconnect the pedals, then click Check updated controller.";
        }
        catch (Exception failure) when (IsExpected(failure)) { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.Status = $"Firmware update needs review: {failure.Message}"; }
        finally
        {
            viewModel.Firmware.Plan = null;
            var detail = viewModel.Firmware.Status;
            await CheckFirmwareStartupAsync();
            viewModel.Firmware.Status = detail;
            viewModel.Firmware.Running = false;
            await ResumeLiveAfterRecoveryAsync();
        }
    });
    private async void CheckUpdatedFirmwareClick(object sender, RoutedEventArgs e) => await CheckUpdatedFirmwareAsync(false);
    private async void AcceptUpdatedFirmwareClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Localizer.Translate("Accept the detected firmware and current hardware calibration? This releases virtual output. An interrupted programming result is not a verified flash; retained backups and logs remain available."), Localizer.Translate("Accept detected controller"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK)
        { await CheckUpdatedFirmwareAsync(true); }
    }
    internal Task CheckUpdatedFirmwareAsync(bool accept) => RunAsync(async () =>
    {
        if (desktop.Firmware is null || hardwareSession is not null) { return; }
        try
        {
            var result = await Task.Run(() => desktop.Firmware.CheckConnectionAsync(accept, lifetime.Token));
            viewModel.Firmware.Blocked = !result.Complete; viewModel.Firmware.CanAccept = result.CanAccept;
            viewModel.Firmware.Status = result.Detail;
            if (result.Complete) { await CheckStartupRecoveryAsync(); await ResumeLiveAfterRecoveryAsync(); await CheckDeviceDiagnosticsAsync(); }
        }
        catch (Exception failure) when (IsExpected(failure))
        { desktop.Log?.Write("firmware", "An operation needs attention.", failure); viewModel.Firmware.CanAccept = false; viewModel.Firmware.Status = $"Firmware update needs review: {failure.Message}"; }
    });
}
