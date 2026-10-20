using System.IO;
using System.Windows;
using Microsoft.Win32;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Hardware;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private HardwareCalibrationSession? hardwareSession;
    private HardwareCalibrationView? cachedCalibrationView;
    private Task? modeOperation;
    private CancellationTokenSource? connectionCancellation;
    private SidePanelMode requestedPanel;
    private bool WantsCalibration => requestedPanel == SidePanelMode.Calibration;
    private readonly Func<PedalChannel, ResourceDictionary, CancellationToken, Task<HardwareCalibrationSession>> createHardwareSession;

    private async void TuningModeClick(object sender, RoutedEventArgs e) => await RequestPanelModeAsync(false);
    private async void CalibrationModeClick(object sender, RoutedEventArgs e) => await RequestPanelModeAsync(true);
    private async void HardwareControlClick(object sender, RoutedEventArgs e) => await RequestPanelModeAsync(true);

    private Task RequestPanelModeAsync(bool calibration) => RequestPanelModeAsync(calibration ? SidePanelMode.Calibration : SidePanelMode.Tuning);

    internal async Task RequestPanelModeAsync(SidePanelMode mode)
    {
        if (closing || !viewModel.CanEdit) { return; }
        if (mode == SidePanelMode.Calibration && viewModel.Recovery.IsBlocked)
        { viewModel.ShowMessage("Resolve the interrupted calibration before opening a new hardware session."); return; }
        if (modeOperation is not { IsCompleted: false } && mode == viewModel.PanelMode
            && (mode != SidePanelMode.Calibration || hardwareSession is not null)) { return; }
        requestedPanel = mode;
        if (!WantsCalibration)
        {
            connectionCancellation?.Cancel();
        }
        if (modeOperation is { IsCompleted: false }) { return; }
        var operation = ProcessPanelModeAsync();
        modeOperation = operation;
        try { await operation; }
        finally { if (ReferenceEquals(modeOperation, operation)) { modeOperation = null; } }
    }

    private async Task ProcessPanelModeAsync()
    {
        viewModel.IsSwitchingMode = true;
        try
        {
            while (!closing)
            {
                if (WantsCalibration)
                {
                    if (hardwareSession is null)
                    {
                        using var connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        connectionCancellation = connection;
                        Localization.Localizer.Bind(CalibrationConnectionInfo, System.Windows.Controls.TextBlock.TextProperty, "Connecting to controller… You can switch back to Tuning while this completes.");
                        viewModel.SourceName = "Calibration · connecting controller";
                        viewModel.ShowMessage("Connecting to controller…");
                        try
                        {
                            await PrepareHardwareAsync();
                            connection.Token.ThrowIfCancellationRequested();
                            await ConnectCalibrationAsync(connection.Token);
                        }
                        catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
                        catch (Exception failure) when (OperationErrors.IsExpected(failure))
                        {
                            desktop.Log?.Write("hardware", "An operation needs attention.", failure);
                            if (!WantsCalibration || closing) { continue; }
                            CalibrationHost.Visibility = Visibility.Hidden;
                            CalibrationWelcome.Visibility = Visibility.Visible;
                            viewModel.HardwareDisplay = null;
                            CalibrationConnectionInfo.Text = failure.Message;
                            SetPanelMode(true);
                            viewModel.ShowMessage(failure.Message);
                            break;
                        }
                        finally { connectionCancellation = null; }
                        if (hardwareSession is null && connection.IsCancellationRequested && WantsCalibration && !closing) { continue; }
                    }
                    if (WantsCalibration) { SetPanelMode(true); break; }
                }
                else
                {
                    var hadSession = hardwareSession is not null;
                    if (hadSession) { viewModel.ShowMessage("Restoring unsaved controller changes…"); }
                    if (!await EndCalibrationAsync())
                    {
                        requestedPanel = SidePanelMode.Calibration;
                        SetPanelMode(true);
                        viewModel.ShowMessage("Controller restoration needs attention. Use the calibration recovery controls.");
                        break;
                    }
                    if (WantsCalibration) { continue; }
                    if (closing) { break; }
                    if (hadSession) { await CheckStartupRecoveryAsync(); }
                    await ResumeLiveAfterRecoveryAsync();
                    SetPanelMode(requestedPanel);
                    if (requestedPanel == SidePanelMode.Settings) { _ = CheckDeviceDiagnosticsAsync(); }
                    if (hadSession && runtime is null) { engine.End("Stopped"); }
                    break;
                }
            }
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        {
            desktop.Log?.Write("hardware", "An operation needs attention.", failure);
            if (hardwareSession is not null) { requestedPanel = SidePanelMode.Calibration; SetPanelMode(true); }
            viewModel.ShowMessage(failure.Message);
        }
        finally
        {
            if (hardwareSession is null && !viewModel.IsCalibrationMode)
            { viewModel.HardwareDisplay = null; CalibrationHost.Visibility = Visibility.Hidden; CalibrationWelcome.Visibility = Visibility.Visible; }
            viewModel.IsSwitchingMode = false;
            viewModel.Refresh();
        }
    }

    private void SetPanelMode(bool calibration) => SetPanelMode(calibration ? SidePanelMode.Calibration : SidePanelMode.Tuning);

    private void SetPanelMode(SidePanelMode mode)
    {
        if (viewModel.PanelMode == mode) { return; }
        var graphSourceChanged = viewModel.IsCalibrationMode != (mode == SidePanelMode.Calibration);
        viewModel.PanelMode = mode;
        // Hidden panels retain their layout; the graph and cards change source in this same dispatcher turn.
        TuningSidePanel.Visibility = mode == SidePanelMode.Tuning ? Visibility.Visible : Visibility.Hidden;
        CalibrationSidePanel.Visibility = mode == SidePanelMode.Calibration ? Visibility.Visible : Visibility.Hidden;
        SettingsSidePanel.Visibility = mode == SidePanelMode.Settings ? Visibility.Visible : Visibility.Hidden;
        if (graphSourceChanged) { viewModel.GraphPaused = false; MainSignalPlot.ResetZoom(); }
        viewModel.Refresh(force: true);
    }

    private async Task ConnectCalibrationAsync(CancellationToken token)
    {
        var selectedChannel = viewModel.SelectedAxis switch
        {
            PedalAxis.Throttle => PedalChannel.Throttle,
            PedalAxis.Brake => PedalChannel.Brake,
            _ => PedalChannel.Clutch
        };
        var session = await createHardwareSession(selectedChannel, Resources, token);
        if (token.IsCancellationRequested || !WantsCalibration || closing)
        {
            await session.DisposeAsync();
            return;
        }
        hardwareSession = session;
        session.View.Log = desktop.Log;
        cachedCalibrationView = session.View;
        session.View.Completed += CalibrationCompleted;
        session.View.RestoreBackupRequested += RestoreHardwareBackupClick;
        session.View.BackupRequested += BackupHardwareClick;
        viewModel.HardwareDisplay = (HardwarePedalViewModel)session.View.DataContext;
        viewModel.HardwareDisplay.PropertyChanged += HardwareDisplayChanged;
        if (!ReferenceEquals(CalibrationHost.Content, session.View)) { CalibrationHost.Content = session.View; }
        CalibrationHost.Visibility = Visibility.Visible;
        CalibrationWelcome.Visibility = Visibility.Hidden;
        await session.View.ActivateAsync();
        viewModel.SourceName = "Calibration · live controller counts";
        viewModel.ShowMessage("Calibration connected. The same graph now displays controller counts. Save to device is separate.");
    }

    private async void CalibrationCompleted(object? sender, EventArgs e) => await RequestPanelModeAsync(false);

    private async Task<bool> EndCalibrationAsync(bool switchToTuning = true)
    {
        if (hardwareSession is null) { return true; }
        if (!await hardwareSession.View.TryCloseAsync()) { return false; }
        var session = hardwareSession;
        // Switch away before releasing the display model, avoiding a frame of empty calibration history.
        if (switchToTuning && !closing && !viewModel.IsSwitchingMode) { SetPanelMode(WantsCalibration ? SidePanelMode.Tuning : requestedPanel); }
        await session.DisposeAsync();
        session.View.Completed -= CalibrationCompleted;
        session.View.RestoreBackupRequested -= RestoreHardwareBackupClick;
        session.View.BackupRequested -= BackupHardwareClick;
        hardwareSession = null;
        if (viewModel.HardwareDisplay is { } display) { display.PropertyChanged -= HardwareDisplayChanged; }
        if (closing || viewModel.IsSwitchingMode) { return true; }
        viewModel.HardwareDisplay = null;
        CalibrationHost.Visibility = Visibility.Hidden;
        CalibrationWelcome.Visibility = Visibility.Visible;
        Localization.Localizer.Bind(CalibrationConnectionInfo, System.Windows.Controls.TextBlock.TextProperty, "Connect a pedal controller to calibrate it. Close FEEL-VR Control and games first.");
        viewModel.HardwareInfo = session.BackupPath is { } path && File.Exists(path) ? $"Hardware recovery backup retained:\n{path}"
            : "Calibration session ended. Connect again to inspect controller bounds.";
        viewModel.SourceName = "No source selected";
        viewModel.ShowMessage("Controller changes restored. Review profile mapping and deadzones, then Start live. Virtual output stays disabled.");
        return true;
    }

    private void HardwareDisplayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HardwarePedalViewModel.SelectedChannel) && sender is HardwarePedalViewModel model)
        { viewModel.SelectedAxis = model.SelectedChannel switch { PedalChannel.Throttle => PedalAxis.Throttle, PedalChannel.Brake => PedalAxis.Brake, _ => PedalAxis.Clutch }; }
    }

    private async void BackupHardwareClick(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Filter = Localization.Localizer.Translate("SherpaAxis hardware backup (*.json)|*.json"),
            DefaultExt = ".json",
            AddExtension = true,
            FileName = $"pedals-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Title = Localization.Localizer.Translate("Backup current hardware settings"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (picker.ShowDialog(this) != true) { return; }
        await RunAsync(async () =>
        {
            if (modeOperation is { } pending) { await pending; }
            if (hardwareSession is null)
            {
                await PrepareHardwareAsync();
                await ConnectCalibrationAsync(lifetime.Token);
            }
            if (hardwareSession is null) { throw new InvalidOperationException("Connect the controller before backing up hardware."); }
            var backup = await hardwareSession.View.CreateHardwareBackupAsync(lifetime.Token);
            await new HardwareRecoveryStore(picker.FileName).WriteAsync(backup, lifetime.Token);
            viewModel.ShowMessage($"Hardware backup saved: {picker.FileName}. Controller settings were not changed.");
        });
    }
    private async void RestoreHardwareBackupClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = Localization.Localizer.Translate("SherpaAxis hardware backup (*.json)|*.json"), InitialDirectory = HardwareRecoveryStore.DefaultDirectory, Title = Localization.Localizer.Translate("Restore a hardware calibration backup to the same controller") };
        if (picker.ShowDialog(this) != true) { return; }
        await RunAsync(async () =>
        {
            if (modeOperation is { } pending) { await pending; }
            if (!await EndCalibrationAsync(switchToTuning: false)) { throw new InvalidOperationException("Restore this session or keep its recovery backup before loading another backup."); }
            await PrepareHardwareAsync();
            var backup = await HardwareRecoveryStore.ReadAsync(picker.FileName, lifetime.Token);
            if (string.IsNullOrWhiteSpace(backup.SerialNumber)) { throw new InvalidOperationException("Backup has no verified controller serial identity and requires manual review."); }
            await using var features = new WindowsHidFeatureConnection(backup.Device);
            using var identityDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            identityDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            var identity = await features.ReadIdentityAsync(identityDeadline.Token);
            var actual = await new CalibrationSnapshotReader(new WindowsHidInputSource(), features).ReadAsync(TimeSpan.FromSeconds(2), backup.Device.Path, lifetime.Token);
            if (actual.Device != backup.Device) { throw new InvalidOperationException("Backup belongs to a different controller collection."); }
            using var transaction = backup.IsManualBackup
                ? new PedalConfigurationTransaction(actual, features, features,
                    new HardwareRecoveryStore(Path.Combine(HardwareRecoveryStore.DefaultDirectory, $"restore-{Guid.NewGuid():N}.json")), TimeSpan.FromSeconds(2), identity.SerialNumber)
                : PedalConfigurationTransaction.FromRecovery(backup, features, features, new HardwareRecoveryStore(picker.FileName), TimeSpan.FromSeconds(2), identity.SerialNumber);
            if (backup.IsManualBackup) { await transaction.RestoreHardwareBackupAsync(backup, lifetime.Token); }
            else { await transaction.RestoreSavedAsync(lifetime.Token); }
            viewModel.HardwareInfo = "Backup hardware bounds restored and read back successfully.";
            viewModel.ShowMessage(transaction.PersistenceUncertain ? "Volatile hardware bounds restored. Persistent Save remains uncertain; backup retained. Review and Save explicitly in Hardware settings."
                : backup.IsManualBackup ? "Hardware backup restored and verified. Connect to review, then Save to device to persist. The manual backup was retained."
                : "Hardware recovery completed and verified. Review profiles before Start live.");
        });
    }

    private async Task PrepareHardwareAsync()
    {
        StopHold();
        viewModel.CancelCalibration();
        engine.SuspendOutput("calibration panel opened");
        if (runtime is not null) { await runtime.PauseAsync(); }
        await source.StopAsync();
        if (hardwareSession is null) { await CheckStartupRecoveryAsync(); }
        if (viewModel.Recovery.IsBlocked) { throw new InvalidOperationException("Resolve the interrupted calibration before opening a new hardware session."); }
        engine.End("Calibration mode · main input stopped");
    }
}
