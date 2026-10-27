using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App;

internal enum HardwareCalibrationAction { Capture, Cancel, ApplyBounds, Defaults, Save, Restore, Abandon }

public partial class HardwareCalibrationView : UserControl, IAsyncDisposable
{
    private PedalConfigurationTransaction configuration = null!;
    private PedalHardwareCalibration calibration = null!;
    private HardwarePedalViewModel model = null!;
    private Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> input = null!;
    private readonly DispatcherTimer refresh;
    private CancellationTokenSource? inputCancellation;
    private Task? inputWorker;
    private string? inputFailure;
    private bool ended;
    private bool disposed = true;
    private TaskCompletionSource? operationFinished;
    internal string? ControllerSerial { get; private set; }
    public DiagnosticLog? Log { get; set; }
    public Func<PedalChannel, HardwareCalibrationRange, HardwareCalibrationRange, Task>? StoreSensorReference { get; set; }
    internal Func<HardwareCalibrationAction, Task>? RequestOperation { get; set; }

    public event EventHandler? Completed;
    public event RoutedEventHandler? RestoreBackupRequested;
    public event RoutedEventHandler? BackupRequested;

    public HardwareCalibrationView(PedalConfigurationTransaction configuration,
        Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> input, ResourceDictionary theme, HidDeviceIdentity? identity = null,
        PedalChannel selectedChannel = PedalChannel.Brake) : this(theme)
        => Reconnect(configuration, input, identity, selectedChannel);

    public HardwareCalibrationView(ResourceDictionary theme)
    {
        InitializeComponent();
        Resources.MergedDictionaries.Add(theme);
        model = new();
        DataContext = model;
        refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) =>
        {
            model.Refresh();
            if (Volatile.Read(ref inputFailure) is { } failure) { model.Status = failure; }
        }, Dispatcher);
        ShowPreview();
    }

    internal void ShowPreview(PedalCalibration? bounds = null, HidDeviceIdentity? identity = null)
    {
        if (model.HasSession) { return; }
        var wasClosed = disposed || ended;
        disposed = false;
        ended = false;
        var changedController = identity is not null && ControllerSerial is not null && !PedalDeviceIdentity.SameController(identity.SerialNumber, ControllerSerial);
        model.Preview(bounds, changedController);
        if (identity is not null) { UpdateIdentity(identity); }
        if (wasClosed) { model.Status = "Live pedal output continues. Hardware operations verify the controller before making changes."; }
        model.Refresh();
        refresh.Stop();
    }

    public void Reconnect(PedalConfigurationTransaction configuration,
        Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> input, HidDeviceIdentity? identity = null,
        PedalChannel selectedChannel = PedalChannel.Brake)
    {
        Dispatcher.VerifyAccess();
        if (model.HasSession || inputWorker is not null) { throw new InvalidOperationException("Close the previous hardware session before reusing its controls."); }
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(input);
        this.configuration = configuration;
        this.input = input;
        calibration = new(configuration);
        var sameController = identity is null ? ControllerSerial is null
            : ControllerSerial is not null && PedalDeviceIdentity.SameController(identity.SerialNumber, ControllerSerial);
        model.Connect(configuration, calibration, sameController);
        model.SelectedChannel = selectedChannel;
        if (identity is not null)
        {
            UpdateIdentity(identity);
        }
        DataContext = model;
        inputFailure = null;
        ended = false;
        disposed = false;
        refresh.Start();
        Loaded -= StartInput;
        Loaded += StartInput;
        model.Refresh();
    }

    private void UpdateIdentity(HidDeviceIdentity identity)
    {
        ControllerSerial = identity.SerialNumber;
        var device = PedalDeviceIdentity.Parse(identity.SerialNumber);
        model.IdentityInfo = $"{identity.Product} · {identity.Manufacturer}\nHardware {device.Hardware} · Firmware {device.Firmware}\nSerial: {device.Serial}";
    }

    private async void StartInput(object sender, RoutedEventArgs e) { Loaded -= StartInput; await ActivateAsync(); }

    internal Task ActivateAsync() => model.HasSession && inputCancellation is null ? RunAsync(RestartInputAsync) : Task.CompletedTask;

    private async Task RestartInputAsync()
    {
        await StopInputAsync();
        inputFailure = null;
        model.ClearInput();
        inputCancellation = new();
        var token = inputCancellation.Token;
        inputWorker = Task.Run(async () =>
        {
            try
            {
                var connection = -1;
                var connectedAt = Stopwatch.GetTimestamp();
                await foreach (var item in input(token).WithCancellation(token).ConfigureAwait(false))
                {
                    if (item.State == PedalConnectionState.Connected && connection != item.ConnectionNumber)
                    { connection = item.ConnectionNumber; connectedAt = Stopwatch.GetTimestamp(); }
                    if (item.Axes is not null && Stopwatch.GetElapsedTime(connectedAt) < TimeSpan.FromMilliseconds(100)) { continue; }
                    model.Accept(item);
                }
                if (!token.IsCancellationRequested) { throw new IOException("Hardware input ended unexpectedly."); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception failure) when (OperationErrors.IsExpected(failure))
            {
                Log?.Write("calibration-input", "Calibration input failed.", failure);
                Volatile.Write(ref inputFailure, failure.Message);
                calibration.Observe(new(PedalConnectionState.Disconnected, TimeSpan.Zero, 0));
            }
        });
    }

    private async Task StopInputAsync()
    {
        if (inputCancellation is not null) { await inputCancellation.CancelAsync(); }
        if (inputWorker is not null) { await inputWorker; }
        inputCancellation?.Dispose();
        inputCancellation = null;
        inputWorker = null;
    }

    private async void CaptureClick(object sender, RoutedEventArgs e) => await ExecuteAsync(HardwareCalibrationAction.Capture);
    private async void CancelCaptureClick(object sender, RoutedEventArgs e) => await ExecuteAsync(HardwareCalibrationAction.Cancel);
    private async void ApplyBoundsClick(object sender, RoutedEventArgs e) => await ExecuteAsync(HardwareCalibrationAction.ApplyBounds);
    private async void DefaultsClick(object sender, RoutedEventArgs e) => await ExecuteAsync(HardwareCalibrationAction.Defaults);
    private async void SaveClick(object sender, RoutedEventArgs e) => await ExecuteAsync(HardwareCalibrationAction.Save);
    private async void RestoreClick(object sender, RoutedEventArgs e) => await ExecuteAsync(HardwareCalibrationAction.Restore);
    private async void AbandonClick(object sender, RoutedEventArgs e) => await ExecuteAsync(HardwareCalibrationAction.Abandon);

    internal Task ExecuteAsync(HardwareCalibrationAction action)
    {
        if (disposed || ended || model.Busy) { return Task.CompletedTask; }
        return RequestOperation?.Invoke(action) ?? ExecuteConnectedAsync(action);
    }

    internal Task ExecuteConnectedAsync(HardwareCalibrationAction action)
    {
        if (disposed || ended || model.Busy || !model.HasSession) { return Task.CompletedTask; }
        return action switch
        {
            HardwareCalibrationAction.Capture => CaptureAsync(),
            HardwareCalibrationAction.Cancel => ChangeAsync(async () =>
            {
                if (configuration.NeedsRecovery) { await calibration.RestoreSavedAsync(); }
                else { await calibration.CancelAsync(); }
                model.Status = "This pedal's pre-calibration hardware bounds were restored and verified.";
            }, restartInput: RequestOperation is null),
            HardwareCalibrationAction.ApplyBounds => ChangeAsync(async () =>
            {
                await configuration.ChangeAsync(configuration.Current.WithRange(model.SelectedChannel, model.EditedRange()));
                model.Status = "Manual hardware bounds applied and verified. Save is separate.";
            }),
            HardwareCalibrationAction.Defaults => ChangeAsync(async () =>
            {
                await configuration.ChangeAsync(PedalHardwareSettings.Defaults);
                model.Status = "All three hardware ranges reset and checked. Save to controller keeps the change; Discard changes restores the saved ranges.";
            }),
            HardwareCalibrationAction.Save => ChangeAsync(async () =>
            {
                if (calibration.Capture is not null) { throw new InvalidOperationException("Finish or cancel calibration before Save."); }
                await configuration.SaveAsync();
                model.Status = "Controller Save command completed and current bounds were verified. Reconnect to verify persistence.";
            }),
            HardwareCalibrationAction.Restore => ChangeAsync(async () =>
            {
                await calibration.RestoreSavedAsync();
                model.Status = configuration.PersistenceUncertain ? "Volatile bounds restored. Save completion is still uncertain; the backup is retained."
                    : "Saved hardware bounds restored and verified.";
            }),
            HardwareCalibrationAction.Abandon => RunAsync(async () =>
            {
                await StopInputAsync();
                ended = true;
                refresh.Stop();
                Completed?.Invoke(this, EventArgs.Empty);
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    private Task CaptureAsync() => RunAsync(async () =>
    {
        await StopInputAsync();
        try
        {
            if (calibration.Capture is null)
            {
                await calibration.StartAsync(model.SelectedChannel);
                model.Status = "Capture is running. Release and fully press the pedal several times, then choose Finish capture.";
            }
            else
            {
                var (lower, upper) = model.Margins();
                var captured = calibration.Capture!;
                await calibration.CompleteAsync(lower, upper);
                if (StoreSensorReference is not null)
                {
                    await StoreSensorReference(captured.Channel, new(captured.Minimum!.Value, captured.Maximum!.Value),
                        configuration.Current.Range(captured.Channel));
                }
                model.Status = "Range applied and checked. Choose Save to controller to keep it, or Discard changes to undo.";
                model.ReadBounds();
            }
        }
        finally { if (!configuration.NeedsRecovery) { await RestartInputAsync(); } }
    });

    private Task ChangeAsync(Func<Task> action, bool restartInput = true) => RunAsync(async () =>
    {
        await StopInputAsync();
        try { await action(); model.ReadBounds(); }
        finally { if (restartInput && !configuration.NeedsRecovery) { await RestartInputAsync(); } }
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (model.Busy || disposed || ended) { return; }
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operationFinished = finished;
        model.Busy = true;
        try { await action(); }
        catch (Exception failure) when (OperationErrors.IsExpected(failure)) { Log?.Write("calibration", "A hardware operation failed.", failure); model.Status = failure.Message; }
        finally { model.Busy = false; model.Refresh(); operationFinished = null; finished.SetResult(); }
    }

    private void BackupHardwareClick(object sender, RoutedEventArgs e) => BackupRequested?.Invoke(this, e);
    private void RestoreBackupClick(object sender, RoutedEventArgs e) => RestoreBackupRequested?.Invoke(this, e);

    public Task<HardwareRecoverySnapshot> CreateHardwareBackupAsync(CancellationToken cancellationToken = default)
    {
        if (!model.HasSession || model.Busy || calibration.Capture is not null) { throw new InvalidOperationException("Finish or cancel calibration before backing up hardware."); }
        return configuration.CreateHardwareBackupAsync(cancellationToken);
    }

    public async Task<bool> TryCloseAsync()
    {
        if (ended) { return true; }
        refresh.Stop();
        if (!model.HasSession) { ended = true; return true; }
        if (operationFinished is { } pending) { await pending.Task; }
        if (ended) { return true; }
        if (model.Busy) { refresh.Start(); model.Status = "Wait for the current hardware operation before closing."; return false; }
        model.Busy = true;
        try
        {
            await StopInputAsync();
            var previous = configuration.Current;
            await calibration.RestoreSavedAsync();
            if (previous != configuration.Current) { model.ReadBounds(); }
            ended = true;
            refresh.Stop();
            return true;
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure)) { Log?.Write("calibration-restore", "Could not restore controller settings.", failure); refresh.Start(); model.Status = failure.Message; return false; }
        finally { model.Busy = false; model.Refresh(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        disposed = true;
        Loaded -= StartInput;
        refresh.Stop();
        await StopInputAsync();
        if (model.HasSession) { calibration.Dispose(); model.Disconnect(); }
        GC.SuppressFinalize(this);
    }

}
