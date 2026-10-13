using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Services;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App;

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
    public DiagnosticLog? Log { get; set; }

    public event EventHandler? Completed;
    public event RoutedEventHandler? RestoreBackupRequested;
    public event RoutedEventHandler? BackupRequested;

    public HardwareCalibrationView(PedalConfigurationTransaction configuration,
        Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> input, ResourceDictionary theme, HidDeviceIdentity? identity = null,
        PedalChannel selectedChannel = PedalChannel.Brake)
    {
        InitializeComponent();
        Resources.MergedDictionaries.Add(theme);
        refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) =>
        {
            model.Refresh();
            if (Volatile.Read(ref inputFailure) is { } failure) { model.Status = failure; }
        }, Dispatcher);
        Reconnect(configuration, input, identity, selectedChannel);
    }

    public void Reconnect(PedalConfigurationTransaction configuration,
        Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> input, HidDeviceIdentity? identity = null,
        PedalChannel selectedChannel = PedalChannel.Brake)
    {
        Dispatcher.VerifyAccess();
        if (!disposed || inputWorker is not null) { throw new InvalidOperationException("Close the previous hardware session before reusing its controls."); }
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(input);
        this.configuration = configuration;
        this.input = input;
        calibration = new(configuration);
        model = new(configuration, calibration);
        model.SelectedChannel = selectedChannel;
        if (identity is not null)
        {
            var device = PedalDeviceIdentity.Parse(identity.SerialNumber);
            model.IdentityInfo = $"{identity.Product} · {identity.Manufacturer}\nHardware {device.Hardware} · Firmware {device.Firmware}\nSerial: {device.Serial}";
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

    private async void StartInput(object sender, RoutedEventArgs e) { Loaded -= StartInput; await ActivateAsync(); }

    internal Task ActivateAsync() => inputCancellation is null ? RunAsync(RestartInputAsync) : Task.CompletedTask;

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
                await foreach (var item in input(token).WithCancellation(token).ConfigureAwait(false)) { model.Accept(item); }
                if (!token.IsCancellationRequested) { throw new IOException("Hardware input ended unexpectedly."); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception failure) when (Expected(failure))
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

    private async void CaptureClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await StopInputAsync();
        try
        {
            if (calibration.Capture is null)
            {
                await calibration.StartAsync(model.SelectedChannel);
                model.Status = "Calibration mode is active. Release and press the pedal, adjust hardware deadzones if needed, then Done.";
            }
            else
            {
                var (lower, upper) = model.Margins();
                await calibration.CompleteAsync(lower, upper);
                model.Status = "Hardware bounds applied and verified. Save to device to retain them, or Restore saved to undo.";
                model.ReadBounds();
            }
        }
        finally { if (!configuration.NeedsRecovery) { await RestartInputAsync(); } }
    });

    private async void CancelCaptureClick(object sender, RoutedEventArgs e) => await ChangeAsync(async () =>
    {
        await calibration.CancelAsync();
        model.Status = "This pedal's pre-calibration hardware bounds were restored and verified.";
    });

    private async void ApplyBoundsClick(object sender, RoutedEventArgs e) => await ChangeAsync(async () =>
    {
        await configuration.ChangeAsync(configuration.Current.WithRange(model.SelectedChannel, model.EditedRange()));
        model.Status = "Manual hardware bounds applied and verified. Save is separate.";
    });

    private async void DefaultsClick(object sender, RoutedEventArgs e) => await ChangeAsync(async () =>
    {
        await configuration.ChangeAsync(PedalHardwareSettings.Defaults);
        model.Status = "All three hardware ranges reset and verified. Save is separate; Restore saved undoes this change.";
    });

    private async void SaveClick(object sender, RoutedEventArgs e) => await ChangeAsync(async () =>
    {
        if (calibration.Capture is not null) { throw new InvalidOperationException("Finish or cancel calibration before Save."); }
        await configuration.SaveAsync();
        model.Status = "Controller Save command completed and current bounds were verified. Reconnect to verify persistence.";
    });

    private async void RestoreClick(object sender, RoutedEventArgs e) => await ChangeAsync(async () =>
    {
        await calibration.RestoreSavedAsync();
        model.Status = configuration.PersistenceUncertain ? "Volatile bounds restored. Save completion is still uncertain; the backup is retained."
            : "Saved hardware bounds restored and verified.";
    });

    private Task ChangeAsync(Func<Task> action) => RunAsync(async () =>
    {
        await StopInputAsync();
        try { await action(); model.ReadBounds(); }
        finally { if (!configuration.NeedsRecovery) { await RestartInputAsync(); } }
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (model.Busy || disposed || ended) { return; }
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operationFinished = finished;
        model.Busy = true;
        try { await action(); }
        catch (Exception failure) when (Expected(failure)) { Log?.Write("calibration", "A hardware operation failed.", failure); model.Status = failure.Message; }
        finally { model.Busy = false; model.Refresh(); operationFinished = null; finished.SetResult(); }
    }

    private void BackupHardwareClick(object sender, RoutedEventArgs e) => BackupRequested?.Invoke(this, e);
    private void RestoreBackupClick(object sender, RoutedEventArgs e) => RestoreBackupRequested?.Invoke(this, e);

    public Task<HardwareRecoverySnapshot> CreateHardwareBackupAsync(CancellationToken cancellationToken = default)
    {
        if (model.Busy || calibration.Capture is not null) { throw new InvalidOperationException("Finish or cancel calibration before backing up hardware."); }
        return configuration.CreateHardwareBackupAsync(cancellationToken);
    }

    public async Task<bool> TryCloseAsync()
    {
        if (ended) { return true; }
        refresh.Stop();
        if (operationFinished is { } pending) { await pending.Task; }
        if (ended) { return true; }
        if (model.Busy) { refresh.Start(); model.Status = "Wait for the current hardware operation before closing."; return false; }
        model.Busy = true;
        try
        {
            await StopInputAsync();
            await calibration.RestoreSavedAsync();
            ended = true;
            refresh.Stop();
            return true;
        }
        catch (Exception failure) when (Expected(failure)) { Log?.Write("calibration-restore", "Could not restore controller settings.", failure); refresh.Start(); model.Status = failure.Message; return false; }
        finally { model.Busy = false; model.Refresh(); }
    }

    private async void AbandonClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await StopInputAsync();
        ended = true;
        refresh.Stop();
        Completed?.Invoke(this, EventArgs.Empty);
    });

    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        disposed = true;
        Loaded -= StartInput;
        refresh.Stop();
        await StopInputAsync();
        calibration.Dispose();
        GC.SuppressFinalize(this);
    }

    private static bool Expected(Exception failure) => failure is IOException or UnauthorizedAccessException
        or ArgumentException or InvalidOperationException or TimeoutException or OperationCanceledException;
}
