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
    private readonly DashboardEngine engine = new();
    private readonly DashboardViewModel viewModel;
    private readonly DashboardSource source;
    private readonly DispatcherTimer refresh;
    private readonly CancellationTokenSource lifetime = new();
    private PedalAxis? heldAxis;
    private bool canClose;
    private bool closing;
    private bool disposed;
    private Task? activeOperation;
    private RecordingSession? recording;
    private string? recordingPath;
    private bool recordingNoticeShown;
    private readonly ProfileLibrary library = new(ProfileLibrary.DefaultDirectory);
    private bool editingCurve;
    private OutputBridge? bridge;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        viewModel = new(engine);
        source = new(engine);
        DataContext = viewModel;
        refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, RefreshTick, Dispatcher);
        refresh.Start();
        Loaded += LoadLibrary;
    }

    private void RefreshTick(object? sender, EventArgs e)
    {
        if (heldAxis.HasValue && heldAxis.Value == viewModel.SelectedAxis && IsActive)
        {
            engine.AuthorizeRelease(heldAxis.Value);
        }
        else if (heldAxis.HasValue)
        {
            StopHold();
        }

        if (source.Failure is { } failure)
        {
            viewModel.ShowMessage($"Source failed: {failure}");
        }

        UpdateRecordingStatus();
        viewModel.OutputInfo = bridge?.Status ?? "Output disabled";
        viewModel.Refresh();
        if (recording?.Status is { IsComplete: true } status && !recordingNoticeShown)
        {
            recordingNoticeShown = true;
            viewModel.ShowMessage(status.Failure is { } error ? $"Recording failed: {error.Message} Files may be partial."
                : $"Recording complete: {status.WrittenSamples:N0} reports in {Path.GetFileName(recordingPath)} and companion files.");
        }
    }

    private void UpdateRecordingStatus()
    {
        var status = recording?.Status;
        viewModel.CanRecord = source.IsRunning && (status is null || status.IsComplete) && viewModel.CanEdit;
        viewModel.CanFinishRecording = status is { IsComplete: false } && viewModel.CanEdit;
        viewModel.RecordingInfo = status is null ? "Not recording" : status.Failure is not null ? "Recording FAILED · files may be partial"
            : status.IsComplete ? $"Saved {status.WrittenSamples:N0} reports" : status.IsStopping ? "Finishing recording…" : $"● Recording · {status.AcceptedSamples:N0} reports";
    }

    private async void RecordClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "SherpaAxis recording (*.csv)|*.csv",
            DefaultExt = ".csv",
            OverwritePrompt = false,
            InitialDirectory = Directory.Exists("research") ? Path.GetFullPath("research") : "",
            FileName = FormattableString.Invariant($"pedals-{DateTime.Now:yyyyMMdd-HHmmss}.csv"),
            Title = "Record input CSV, processed CSV, and session events (new files only)"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await FinishRecordingAsync();
            if (!source.IsRunning)
            {
                throw new InvalidOperationException("Start live input or playback before recording.");
            }

            recording = await RecordingSession.CreateAsync(dialog.FileName, lifetime.Token);
            recordingPath = dialog.FileName;
            recordingNoticeShown = false;
            try { engine.AttachRecording(recording); }
            catch (Exception exception) when (IsExpected(exception))
            {
                recording.Abort(exception);
                await recording.StopAsync();
                throw;
            }

            viewModel.ShowMessage("Recording every report. Active settings and session events are saved alongside the input and processed CSVs.");
        });
    }

    private async void FinishRecordingClick(object sender, RoutedEventArgs e) => await RunAsync(FinishRecordingAsync);

    private Task FinishRecordingAsync() => FinishRecordingAsync("user-finished");

    private async Task FinishRecordingAsync(string reason)
    {
        engine.DetachRecording(reason);
        if (recording is not null)
        {
            await recording.StopAsync();
        }
    }

    private async void StartLiveClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        StopHold();
        viewModel.CancelCalibration();
        await FinishRecordingAsync("source-switch");
        var monitor = new PedalMonitor(new WindowsHidInputSource(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        await source.StartAsync(token => monitor.WatchAsync(cancellationToken: token), new("live-device-axis"));
        viewModel.SourceName = "Live · FEEL-VR device axes";
        viewModel.HardwareInfo = "Not read for this live session. Read hardware calibration to inspect current stored bounds.";
        viewModel.ShowMessage("Live read-only input. Start with device-range defaults; hardware RAW profiles may use different units.");
    });

    private async void OpenCaptureClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SherpaAxis monitor capture (*.csv)|*.csv", Title = "Open an original monitor capture" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunAsync(async () =>
        {
            StopHold();
            viewModel.CancelCalibration();
            await FinishRecordingAsync("source-switch");
            var speed = viewModel.PlaybackSpeed;
            await source.StartAsync(token => CapturePlayback.ReadAsync(dialog.FileName, speed, token), new("capture-replay", Path.GetFileName(dialog.FileName), speed));
            viewModel.SourceName = $"Replay · {Path.GetFileName(dialog.FileName)} · {speed}×";
            viewModel.ShowMessage("Playback uses recorded timestamps for processing. Confirm release only in independently known released intervals.");
        });
    }

    private async void StopClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        StopHold();
        await source.StopAsync();
        engine.End("Stopped");
        await FinishRecordingAsync();
    });

    private void SelectAxisClick(object sender, RoutedEventArgs e)
    {
        StopHold();
        if (sender is Button { Tag: PedalAxis axis })
        {
            viewModel.SelectedAxis = axis;
        }
    }

    private void ApplyClick(object sender, RoutedEventArgs e) => Run(() => { StopHold(); viewModel.Apply(); });
    private void NewProfileClick(object sender, RoutedEventArgs e) => Run(() => { StopHold(); viewModel.Load(DashboardEngine.DefaultProfile()); });
    private void CaptureMinimumClick(object sender, RoutedEventArgs e) => Run(() => viewModel.CaptureEndpoint(false));
    private void CaptureMaximumClick(object sender, RoutedEventArgs e) => Run(() => viewModel.CaptureEndpoint(true));
    private void ObservedRangeClick(object sender, RoutedEventArgs e) => Run(viewModel.UseObservedRange);
    private void DefaultsClick(object sender, RoutedEventArgs e) => viewModel.ResetEditor();
    private void CalibrateClick(object sender, RoutedEventArgs e) => Run(() => { StopHold(); viewModel.AdvanceCalibration(); });
    private void CancelCalibrationClick(object sender, RoutedEventArgs e) => viewModel.CancelCalibration();
    private void ConfirmBaselineClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        engine.ConfirmBaseline(viewModel.SelectedAxis);
        viewModel.ShowMessage("Baseline requested from the current sample. Hold the release control only while this pedal is fully released.");
    });
    private void DisableCorrectionClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        engine.DisableCompensation(viewModel.SelectedAxis);
        viewModel.ShowMessage("Zero correction requested. Hold the release control while released to ramp the applied correction back to zero.");
    });

    private async void ReadHardwareClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var reader = new CalibrationSnapshotReader(new WindowsHidInputSource(), new WindowsHidFeatureReader());
        var snapshot = await Task.Run(() => reader.ReadAsync(TimeSpan.FromSeconds(2), cancellationToken: lifetime.Token));
        engine.RecordHardware(snapshot);
        static string Range(HardwareCalibrationRange range) => FormattableString.Invariant($"{range.Minimum} → {range.Maximum}{(range.HasUsableRange ? "" : " (invalid range)")}");
        viewModel.HardwareInfo = $"Read {snapshot.CapturedAtUtc.ToLocalTime():HH:mm:ss}\nThrottle  {Range(snapshot.Calibration.Throttle)}\nBrake  {Range(snapshot.Calibration.Brake)}\nClutch  {Range(snapshot.Calibration.Clutch)}\nSnapshot only; changes in FEEL-VR Control require another read.";
        viewModel.ShowMessage($"Read-only hardware snapshot · Throttle {Range(snapshot.Calibration.Throttle)} · Brake {Range(snapshot.Calibration.Brake)} · Clutch {Range(snapshot.Calibration.Clutch)}");
    });

    private void ReleaseMouseDown(object sender, MouseButtonEventArgs e)
    {
        ReleaseHold.Focus();
        if (ReleaseHold.CaptureMouse())
        {
            heldAxis = viewModel.SelectedAxis;
            engine.AuthorizeRelease(heldAxis.Value);
            ReleaseHold.Content = "Release confirmed · applying changes";
        }

        e.Handled = true;
    }

    private void ReleaseMouseUp(object sender, MouseButtonEventArgs e) { StopHold(); e.Handled = true; }
    private void ReleaseLostCapture(object sender, MouseEventArgs e) => StopHold();
    private void ReleaseLostFocus(object sender, KeyboardFocusChangedEventArgs e) => StopHold();
    private void WindowDeactivated(object? sender, EventArgs e) => StopHold();
    private void ReleaseKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && !e.IsRepeat)
        {
            heldAxis = viewModel.SelectedAxis;
            engine.AuthorizeRelease(heldAxis.Value);
            ReleaseHold.Content = "Release confirmed · applying changes";
            e.Handled = true;
        }
    }

    private void ReleaseKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            StopHold();
            e.Handled = true;
        }
    }

    private void StopHold()
    {
        heldAxis = null;
        engine.RevokeRelease();
        ReleaseHold.Content = "Hold to apply · pedal released";
        if (ReleaseHold.IsMouseCaptured)
        {
            ReleaseHold.ReleaseMouseCapture();
        }
    }

    private void Run(Action action)
    {
        if (closing)
        {
            return;
        }

        try { action(); }
        catch (Exception exception) when (IsExpected(exception)) { viewModel.ShowMessage(exception.Message); }
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
        catch (Exception exception) when (IsExpected(exception)) { viewModel.ShowMessage(exception.Message); }
        finally
        {
            activeOperation = null;
            InputManager.Current.PreProcessInput -= ObserveInput;
            viewModel.CanEdit = !closing;
            viewModel.Refresh();
            if (!closing && IsActive && !userMovedFocus && priorFocus is FrameworkElement { IsEnabled: true, IsVisible: true } element) { element.Focus(); }
        }
    }

    private static bool IsExpected(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or TimeoutException or OperationCanceledException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException;

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (canClose)
        {
            return;
        }

        e.Cancel = true;
        if (closing)
        {
            return;
        }

        closing = true;
        await DisposeAsync();
        canClose = true;
        Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        refresh.Stop();
        StopHold();
        await lifetime.CancelAsync();
        await Task.WhenAll(profileSelections.ToArray());
        profileSelectionGate.Dispose();
        if (activeOperation is not null)
        {
            try { await activeOperation; }
            catch (Exception exception) when (IsExpected(exception)) { }
        }

        await source.DisposeAsync();
        engine.SuspendOutput("window closed");
        if (bridge is not null) { await bridge.DisposeAsync(); }
        await FinishRecordingAsync();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
