using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Bridge;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App;

public partial class MainWindow : Window, IAsyncDisposable
{
    private readonly DashboardEngine engine;
    private readonly DashboardViewModel viewModel;
    private readonly DashboardSource source;
    private readonly DispatcherTimer refresh;
    private CancellationTokenSource lifetime = new();
    private bool canClose;
    private bool closeRequestPending;
    private bool closing;
    private bool disposed;
    private Task? disposal;
    private Task? activeOperation;
    private readonly ProfileLibrary library;
    private bool editingCurve;
    private long unsavedRefreshAt;

    public MainWindow() : this(new DashboardEngine()) { }

    public MainWindow(DashboardEngine engine,
        Func<PedalChannel, ResourceDictionary, CancellationToken, Task<HardwareCalibrationSession>>? hardwareSessionFactory = null,
        DesktopServices? desktopServices = null, ProfileLibrary? profileLibrary = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.engine = engine;
        desktop = desktopServices ?? DesktopServices.Default();
        library = profileLibrary ?? new(ProfileLibrary.DefaultDirectory);
        createHardwareSession = hardwareSessionFactory ?? ((channel, theme, token) =>
            HardwareCalibrationSession.ConnectAsync(channel, theme, token, cachedCalibrationView));
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        viewModel = new(engine);
        source = new(engine);
        source.Log = desktop.Log;
        DataContext = viewModel;
        refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, RefreshTick, Dispatcher);
        refresh.Start();
        Loaded += LoadLibrary;
        Loaded += DesktopLoaded;
        StateChanged += WindowStateChanged;
        IsVisibleChanged += (_, _) => UpdateRefreshPolicy();
    }

    private void RefreshTick(object? sender, EventArgs e)
    {
        if (Environment.TickCount64 - unsavedRefreshAt >= 500)
        { unsavedRefreshAt = Environment.TickCount64; viewModel.RefreshUnsavedState(); }
        if (source.Failure is { } failure)
        {
            viewModel.ShowMessage($"Source failed: {failure}");
        }


        viewModel.OutputInfo = viewModel.Firmware.Blocked || viewModel.Firmware.Running ? "Virtual output paused · firmware update needs review"
            : viewModel.Recovery.IsBlocked ? "Virtual output paused · hardware recovery required" : runtime?.Status ?? "Waiting for live input";
        viewModel.Refresh();
        if (!viewModel.IsCalibrationMode && (viewModel.InputConnection != diagnosedConnection || viewModel.IsReceiving != diagnosedReceiving))
        {
            diagnosedConnection = viewModel.InputConnection;
            diagnosedReceiving = viewModel.IsReceiving;
            _ = CheckDeviceDiagnosticsAsync();
        }
    }

    private void SelectAxisClick(object sender, RoutedEventArgs e)
    {
        StopHold();
        if (sender is Button { Tag: PedalAxis axis })
        {
            viewModel.SelectedAxis = axis;
            if (viewModel.IsCalibrationMode && viewModel.HardwareDisplay is { CanSelect: true } hardware)
            { hardware.SelectedChannel = axis switch { PedalAxis.Throttle => PedalChannel.Throttle, PedalAxis.Brake => PedalChannel.Brake, _ => PedalChannel.Clutch }; }
        }
    }

    private void ApplyClick(object sender, RoutedEventArgs e) => Run(() => { StopHold(); viewModel.Apply(); });
    private void NewProfileClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        StopHold();
        viewModel.Load(new(Localization.Localizer.Translate("new profile"), DashboardEngine.DefaultProfile().Axes));
        viewModel.ShowMessage("New profile created. Save to keep it after restart.");
    });
    private void ResetInputMappingClick(object sender, RoutedEventArgs e) => Run(viewModel.ResetInputMapping);
    private async void ReadHardwareClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var reader = new CalibrationSnapshotReader(new WindowsHidInputSource(), new WindowsHidFeatureReader());
        var snapshot = await Task.Run(() => reader.ReadAsync(TimeSpan.FromSeconds(2), cancellationToken: lifetime.Token));
        engine.RecordHardware(snapshot);
        static string Range(HardwareCalibrationRange range) => FormattableString.Invariant($"{range.Minimum} → {range.Maximum}{(range.HasUsableRange ? "" : " (invalid range)")}");
        viewModel.HardwareInfo = $"Read {snapshot.CapturedAtUtc.ToLocalTime():HH:mm:ss}\nThrottle  {Range(snapshot.Calibration.Throttle)}\nBrake  {Range(snapshot.Calibration.Brake)}\nClutch  {Range(snapshot.Calibration.Clutch)}\nSnapshot only; changes in FEEL-VR Control require another read.";
        viewModel.ShowMessage($"Read-only hardware snapshot · Throttle {Range(snapshot.Calibration.Throttle)} · Brake {Range(snapshot.Calibration.Brake)} · Clutch {Range(snapshot.Calibration.Clutch)}");
    });

    private void StopHold() => engine.RevokeRelease();
    private void WindowDeactivated(object? sender, EventArgs e) => StopHold();
    private void Run(Action action)
    {
        if (closing)
        {
            return;
        }

        try { action(); }
        catch (Exception exception) when (IsExpected(exception)) { desktop.Log?.Write("ui-action", "An action could not finish.", exception); viewModel.ShowMessage(exception.Message); }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (!viewModel.CanEdit || closing)
        {
            return;
        }

        var priorFocus = Keyboard.FocusedElement;
        var userMovedFocus = false;
        void ObserveInput(object inputSender, PreProcessInputEventArgs args)
        {
            if (args.StagingItem.Input is MouseButtonEventArgs { ButtonState: MouseButtonState.Pressed } or KeyEventArgs { IsDown: true }) { userMovedFocus = true; }
        }
        InputManager.Current.PreProcessInput += ObserveInput;
        viewModel.CanEdit = false;
        viewModel.Refresh();
        try { activeOperation = action(); await activeOperation; }
        catch (Exception exception) when (IsExpected(exception)) { desktop.Log?.Write("ui-action", "An operation could not finish.", exception); if (!closing) { viewModel.ShowMessage(exception.Message); } }
        finally
        {
            activeOperation = null;
            InputManager.Current.PreProcessInput -= ObserveInput;
            viewModel.CanEdit = !closing;
            if (!closing) { viewModel.Refresh(); }
            if (!closing && IsActive && !userMovedFocus && priorFocus is FrameworkElement { IsEnabled: true, IsVisible: true } element) { element.Focus(); }
        }
    }

    private static bool IsExpected(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or TimeoutException or OperationCanceledException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or System.Security.SecurityException;

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (canClose)
        {
            return;
        }

        e.Cancel = true;
        if (viewModel.Firmware.Running)
        { viewModel.ShowMessage("Firmware updating is active. Keep the pedals connected; closing is available after the operation finishes."); return; }
        if (closing || closeRequestPending)
        {
            return;
        }
        closeRequestPending = true;
        // Let WPF finish cancelling this close event before hiding, restoring, or closing the native window again.
        await Dispatcher.Yield(DispatcherPriority.Normal);
        try
        {
            if (!exitRequested && preferences.MinimizeOnClose && tray is not null)
            {
                await HideToTrayAsync();
                if (!exitRequested) { return; }
            }
            try { await DisposeAsync(); }
            catch (Exception failure) when (IsExpected(failure))
            {
                desktop.Log?.Write("shutdown", "Could not finish closing.", failure);
                exitRequested = false;
                RestoreFromTray();
                viewModel.ShowMessage($"Could not finish closing: {failure.Message}");
                return;
            }
            canClose = true;
            Close();
        }
        finally { closeRequestPending = false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        if (disposal is null || disposal.IsCompleted) { disposal = DisposeCoreAsync(); }
        await disposal;
        GC.SuppressFinalize(this);
    }

    private async Task DisposeCoreAsync()
    {
        if (libraryInitialization is { } loadingLibrary) { await loadingLibrary; }
        // Refuse a lossy exit before stopping input or changing the window's lifecycle state.
        closing = true;
        viewModel.CanEdit = false;
        refresh.Stop();
        StopHold();
        engine.SuspendOutput("window closing");
        runtime?.StopPublishing();
        viewModel.ShowMessage("Closing… Restoring controller settings and stopping live output.");
        connectionCancellation?.Cancel();
        try
        {
            await lifetime.CancelAsync();
            if (desktopInitialization is { } initializing) { await initializing; }
            if (runtimeInitialization is { } startingRuntime) { await startingRuntime; }
            if (recoveryCheck is { } checkingRecovery) { await checkingRecovery; }
            if (diagnosticsCheck is { } checkingDevices) { await checkingDevices; }
            if (settingsOperation is { } savingSettings) { await savingSettings; }
            if (activeOperation is { } pending)
            {
                try { await pending; }
                catch (Exception exception) when (IsExpected(exception)) { }
            }
            if (modeOperation is { } pendingMode) { await pendingMode; }

            // These own separate resources. Start all cleanup before waiting for slow storage or HID restoration.
            var stopInput = source.DisposeAsync().AsTask();
            var stopRuntime = runtime?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            await Task.WhenAll(stopInput, stopRuntime, CloseHardwareAsync());
            CalibrationHost.Content = null;
            cachedCalibrationView = null;
            viewModel.HardwareDisplay = null;
            buttons.Dispose();
            tray?.Dispose();
            tray = null;
            activationSource?.RemoveHook(ActivationMessage);
            activationSource = null;
            if (Application.Current is { } application) { application.SessionEnding -= SessionEnding; }
            lifetime.Dispose();
            disposed = true;
            if (desktopInitialization is not null) { desktop.Log?.Write("lifecycle", "Application closed normally."); }
        }
        catch
        {
            // Failed restoration must leave the recovery controls usable, including a later close attempt.
            lifetime.Dispose();
            lifetime = new();
            closing = false;
            viewModel.CanEdit = true;
            if (runtime is not null && desktop.Runtime is not null)
            {
                runtime = new(engine, source, desktop.Runtime, buttons, desktop.Log);
                if (hardwareSession is null) { await CheckStartupRecoveryAsync(); await ResumeLiveAfterRecoveryAsync(); }
            }
            if (hardwareSession is null) { SetPanelMode(false); viewModel.HardwareDisplay = null; }
            refresh.Start();
            throw;
        }
    }

    private async Task CloseHardwareAsync()
    {
        if (!await EndCalibrationAsync()) { throw new IOException("Restore controller calibration or keep its recovery backup before closing."); }
    }
}
