using System.Globalization;
using SherpaAxis.App.Models;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

public sealed class HardwarePedalViewModel : ObservableObject
{
    private PedalConfigurationTransaction? configuration;
    private PedalHardwareCalibration? calibration;
    private PedalCalibration? previewBounds;
    private readonly object historySync = new();
    private readonly TimeProvider clock;
    private readonly Dictionary<PedalChannel, Queue<PlotSample>> history = Enum.GetValues<PedalChannel>().ToDictionary(channel => channel, _ => new Queue<PlotSample>());
    private long graphStart;
    private double lastPlotAt = double.NegativeInfinity;
    private double historyWindowSeconds = 10;
    private int? graphConnection;
    private bool graphPaused;
    private PedalChannel selectedChannel = PedalChannel.Brake;
    private PedalMonitorEvent? latest;
    private string status = "Reading pedal input. Hardware changes affect the physical controller in every application.";
    private bool busy;
    private long lastInputAt;
    public HardwarePedalViewModel(TimeProvider? timeProvider = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        graphStart = clock.GetTimestamp();
    }

    public HardwarePedalViewModel(PedalConfigurationTransaction configuration, PedalHardwareCalibration calibration, TimeProvider? timeProvider = null)
        : this(timeProvider) => Connect(configuration, calibration);

    public bool HasSession => configuration is not null;
    public PedalAxes? CurrentAxes => HasSession && clock.GetElapsedTime(Volatile.Read(ref lastInputAt)) < TimeSpan.FromMilliseconds(250)
        && calibration?.Capture?.Interrupted != true ? Volatile.Read(ref latest)?.Axes : null;
    public PedalChannel? SensorChannel => calibration?.Capture is { Interrupted: false } capture ? capture.Channel : null;
    public static IReadOnlyList<PedalChannel> Channels { get; } = Enum.GetValues<PedalChannel>();
    public PedalChannel SelectedChannel
    {
        get => selectedChannel;
        set { if (Set(ref selectedChannel, value)) { ReadBounds(); Changed(nameof(GraphLegend)); } }
    }
    public bool GraphPaused { get => graphPaused; set => Set(ref graphPaused, value); }
    public string GraphLegend => calibration?.Capture switch
    {
        { Interrupted: true } => "Capture interrupted · Cancel or restore before continuing · Reconnected input is excluded",
        not null => $"{SelectedChannel}: sensor counts · Other pedals: controller output · No software processing",
        _ => "Controller output counts · Click Calibrate hardware to expose the selected pedal's sensor counts"
    };
    public static IReadOnlyList<PedalHistory> EmptyHistories { get; } = Enum.GetValues<PedalAxis>().Select(axis => new PedalHistory(axis, [], "Controller output")).ToArray();
    public IReadOnlyList<PedalHistory> Histories { get; private set; } = EmptyHistories;
    public string Minimum { get; set; } = "";
    public string Maximum { get; set; } = "";
    public string LowerDeadzone { get; set; } = "5";
    public string UpperDeadzone { get; set; } = "5";
    public bool Busy { get => busy; set { if (Set(ref busy, value)) { NotifyState(); } } }
    public string Status { get => status; set => Set(ref status, value); }
    public bool IsCapturing => calibration?.Capture is not null;
    public bool CanSelect => !Busy && !IsCapturing && configuration?.NeedsRecovery != true;
    public bool CanCapture => !Busy && configuration?.NeedsRecovery != true && configuration?.PersistenceUncertain != true;
    public bool CanSave => HasSession && !Busy && !IsCapturing && configuration?.NeedsRecovery != true;
    public bool CanRestore => !Busy;
    public bool CanBackup => CanSelect && configuration?.PersistenceUncertain != true;
    public bool CanSetMargins => !Busy && IsCapturing;
    public bool CanAbandon => !Busy && configuration?.NeedsRecovery == true;
    public string CaptureButtonText => IsCapturing ? "Done" : "Calibrate hardware";
    public string RangeInfo { get; private set; } = "";
    public string DeviceInfo { get; private set; } = "";
    public string StateInfo { get; private set; } = "";
    private string identityInfo = "";
    public string IdentityInfo { get => identityInfo; set => Set(ref identityInfo, value); }

    public void Connect(PedalConfigurationTransaction transaction, PedalHardwareCalibration capture, bool preserveEdits = true)
    {
        var sameBounds = preserveEdits && previewBounds == transaction.Current;
        configuration = transaction;
        calibration = capture;
        previewBounds = transaction.Current;
        if (!sameBounds) { ReadBounds(); }
        Changed(nameof(HasSession));
        Refresh();
    }

    public void Disconnect()
    {
        previewBounds = configuration?.Current ?? previewBounds;
        configuration = null;
        calibration = null;
        ClearInput();
        Changed(nameof(HasSession));
    }

    public void Preview(PedalCalibration? bounds, bool resetEdits = false)
    {
        if (HasSession || bounds is null || !resetEdits && bounds == previewBounds) { return; }
        previewBounds = bounds;
        ReadBounds();
    }

    public void Accept(PedalMonitorEvent item)
    {
        calibration?.Observe(item);
        if (item.Axes is not null) { Volatile.Write(ref lastInputAt, clock.GetTimestamp()); }
        Volatile.Write(ref latest, item);
        var interrupted = calibration?.Capture?.Interrupted == true;
        lock (historySync)
        {
            if (interrupted || item.State == PedalConnectionState.Disconnected) { ClearHistory(); return; }
            if (item.Axes is not { } axes) { return; }
            if (graphConnection != item.ConnectionNumber) { ClearHistory(); graphConnection = item.ConnectionNumber; }
            var seconds = clock.GetElapsedTime(graphStart).TotalSeconds;
            if (seconds - lastPlotAt < .02) { return; }
            lastPlotAt = seconds;
            foreach (var (channel, samples) in history)
            {
                var value = channel switch { PedalChannel.Throttle => axes.Throttle, PedalChannel.Brake => axes.Brake, _ => axes.Clutch };
                samples.Enqueue(new(seconds, 0, 0, 0, value));
                while (samples.Count > 1 && (samples.Count > 3002 || samples.Peek().Time < seconds - historyWindowSeconds)) { samples.Dequeue(); }
            }
        }
    }
    public void ClearInput()
    {
        Volatile.Write(ref latest, null);
        lock (historySync) { ClearHistory(); }
        GraphPaused = false;
        Refresh();
    }

    public void SetGraphWindow(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds is < .1 or > 60) { throw new ArgumentOutOfRangeException(nameof(seconds)); }
        lock (historySync) { historyWindowSeconds = seconds; }
    }
    private void ClearHistory()
    {
        foreach (var samples in history.Values) { samples.Clear(); }
        graphStart = clock.GetTimestamp();
        lastPlotAt = double.NegativeInfinity;
        graphConnection = null;
    }

    public void Refresh()
    {
        var capture = calibration?.Capture;
        if (!GraphPaused)
        {
            lock (historySync)
            {
                Histories = history.Select(pair => new PedalHistory(ToAxis(pair.Key), pair.Value.ToArray(),
                    capture?.Channel == pair.Key ? "Sensor counts · calibration mode" : "Controller output")).ToArray();
            }
            Changed(nameof(Histories));
        }
        Changed(nameof(GraphLegend));
        var input = Volatile.Read(ref latest);
        var axes = input?.Axes;
        var value = axes.HasValue ? selectedChannel switch
        {
            PedalChannel.Throttle => axes.Value.Throttle,
            PedalChannel.Brake => axes.Value.Brake,
            _ => axes.Value.Clutch
        } : (int?)null;
        var range = !HasSession ? "Live input and output continue in the main graph."
            : capture is null ? $"Controller output: {value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—"}"
            : capture.Interrupted ? "Capture interrupted · Cancel or restore before continuing."
            : capture.Reports == 0 ? "Waiting for input in calibration mode…"
            : FormattableString.Invariant($"0% = {capture.Minimum:N0}\n100% = {capture.Maximum:N0}\nSensor input: {capture.Current:N0}");
        if (RangeInfo != range) { RangeInfo = range; Changed(nameof(RangeInfo)); }
        var bounds = configuration?.Current ?? previewBounds;
        var boundsLabel = capture is not null ? "Temporary calibration bounds (sensor counts)"
            : !HasSession ? "Last known hardware calibration bounds (sensor counts)"
            : bounds == PedalHardwareSettings.Defaults ? "Full-scale defaults — calibrated limits are not set."
            : "Hardware calibration bounds (sensor counts)";
        DeviceInfo = bounds is null ? "Controller bounds are read when a hardware operation starts." : boundsLabel + "\n" + string.Join("\n", Channels.Select(channel =>
        {
            var range = bounds.Range(channel);
            return FormattableString.Invariant($"{channel}: {range.Minimum:N0} → {range.Maximum:N0}");
        }));
        StateInfo = !HasSession ? "Live pedal output continues. Hardware operations verify the controller before making changes."
            : configuration!.NeedsRecovery ? "Recovery required · keep the backup and reconnect the same controller."
            : configuration.PersistenceUncertain ? "Save completion uncertain · backup retained. Restore and explicitly Save after review."
            : capture?.Interrupted == true ? "Input connection changed · cancel and restore this capture."
            : capture is not null ? "Temporary full-range hardware calibration · Save is disabled."
            : configuration.IsDirty ? "Hardware settings changed for this session · Save to device to retain them."
            : "No unsaved hardware changes.";
        Changed(nameof(DeviceInfo));
        Changed(nameof(StateInfo));
        NotifyState();
    }

    public void ReadBounds()
    {
        var range = (configuration?.Current ?? previewBounds)?.Range(selectedChannel);
        Minimum = range?.Minimum.ToString(CultureInfo.InvariantCulture) ?? "";
        Maximum = range?.Maximum.ToString(CultureInfo.InvariantCulture) ?? "";
        Changed(nameof(Minimum));
        Changed(nameof(Maximum));
        Refresh();
    }

    public HardwareCalibrationRange EditedRange() => new(Integer(Minimum), Integer(Maximum));
    public (decimal Lower, decimal Upper) Margins() => (Percent(LowerDeadzone), Percent(UpperDeadzone));

    private static PedalAxis ToAxis(PedalChannel channel) => channel switch
    { PedalChannel.Throttle => PedalAxis.Throttle, PedalChannel.Brake => PedalAxis.Brake, _ => PedalAxis.Clutch };

    private void NotifyState()
    {
        foreach (var name in new[] { nameof(IsCapturing), nameof(CanSelect), nameof(CanCapture), nameof(CanSave), nameof(CanRestore),
            nameof(CanSetMargins), nameof(CanAbandon), nameof(CanBackup), nameof(CaptureButtonText) }) { Changed(name); }
    }

    private static int Integer(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
        ? result : throw new ArgumentException("Hardware bounds require integer values.");
    private static decimal Percent(string value) => decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result)
        ? result : throw new ArgumentException("Hardware deadzones require a percentage using a decimal point.");
}
