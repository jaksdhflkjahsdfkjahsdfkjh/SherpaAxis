using SherpaAxis.App.Models;
using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    public static string VersionLabel { get; } = $"v{typeof(DashboardViewModel).Assembly.GetName().Version!.ToString(3)}";
    public GettingStartedViewModel Tour { get; } = new();
    private readonly DashboardEngine engine;
    private readonly Dictionary<PedalAxis, AxisEditor> editors = [];
    private PedalProfile applied = DashboardEngine.DefaultProfile();
    private PedalAxis selectedAxis = PedalAxis.Brake;
    private string profileName = "Device-axis preview";
    private bool plotCounts;
    private ProfileTab? selectedProfile;
    public DashboardViewModel(DashboardEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.engine = engine;
        Load(applied);
        selectedProfile!.Recoverable = false;
        MarkSelectedSaved();
        ShowMessage("Connecting to the pedals automatically. Select a profile or tune the pedals; hardware calibration is separate.");
    }

    public IReadOnlyList<AxisCardViewModel> Cards { get; } = Enum.GetValues<PedalAxis>().Select(axis => new AxisCardViewModel(axis)).ToArray();
    public static IReadOnlyList<PedalAxis> AxisNames { get; } = Enum.GetValues<PedalAxis>();
    private bool canEdit = true;
    public bool CanEdit { get => canEdit; set { if (Set(ref canEdit, value)) { Changed(nameof(CanTune)); } } }
    private SidePanelMode panelMode;
    private bool isSwitchingMode;
    private sealed record DisplaySnapshot(string Mode, string Legend, IReadOnlyList<PedalHistory> Histories, string SampleInfo);
    private DisplaySnapshot? transitionDisplay;
    private HardwarePedalViewModel? hardwareDisplay;
    public SidePanelMode PanelMode
    {
        get => panelMode;
        set
        {
            if (Set(ref panelMode, value))
            { Changed(nameof(IsCalibrationMode)); Changed(nameof(IsSettingsMode)); Changed(nameof(IsTuningMode)); Changed(nameof(CanTune)); NotifyDisplay(); }
        }
    }
    public bool IsCalibrationMode { get => PanelMode == SidePanelMode.Calibration; set => PanelMode = value ? SidePanelMode.Calibration : SidePanelMode.Tuning; }
    public bool IsSettingsMode => PanelMode == SidePanelMode.Settings;
    public bool IsTuningMode => PanelMode == SidePanelMode.Tuning;
    public PreferencesViewModel Preferences { get; } = new();
    public StartupRecoveryViewModel Recovery { get; } = new();
    public DeviceDiagnosticsViewModel Diagnostics { get; } = new();
    public FirmwareUpdateViewModel Firmware { get; } = new();
    private bool vendorControlRunning;
    public bool VendorControlRunning { get => vendorControlRunning; set => Set(ref vendorControlRunning, value); }
    public int InputConnection { get; private set; }
    public bool IsSwitchingMode
    {
        get => isSwitchingMode;
        set
        {
            if (value == isSwitchingMode) { return; }
            transitionDisplay = value ? new(DisplayGraphMode, DisplayGraphLegend, DisplayHistories, DisplaySampleInfo) : null;
            Set(ref isSwitchingMode, value);
            Changed(nameof(CanTune));
            if (!value) { NotifyDisplay(); }
        }
    }
    internal void CommitDisplayTransition() => transitionDisplay = null;
    public bool CanTune => CanEdit && !IsCalibrationMode && !IsSwitchingMode;
    public HardwarePedalViewModel? HardwareDisplay { get => hardwareDisplay; set { if (Set(ref hardwareDisplay, value)) { value?.SetGraphWindow(GraphWindow); NotifyDisplay(); } } }
    public string DisplayGraphMode => transitionDisplay?.Mode ?? (IsCalibrationMode ? "Device counts" : GraphMode);
    public string DisplayGraphLegend => transitionDisplay?.Legend ?? (IsCalibrationMode ? HardwareDisplay?.GraphLegend ?? "Calibration · connect a controller to view live counts" : GraphLegend);
    public IReadOnlyList<PedalHistory> DisplayHistories => transitionDisplay?.Histories ?? (IsCalibrationMode ? HardwareDisplay?.Histories ?? HardwarePedalViewModel.EmptyHistories : PedalHistories);
    public string DisplaySampleInfo => transitionDisplay?.SampleInfo ?? (IsCalibrationMode ? HardwareDisplay?.StateInfo ?? "Waiting for controller" : SampleInfo);
    private string outputInfo = "Waiting for live input";
    public string OutputInfo { get => outputInfo; set => Set(ref outputInfo, value); }
    public bool PlotCounts
    {
        get => plotCounts;
        set { plotCounts = value; GraphMode = value ? "Device counts" : "Input vs filtered"; Changed(); }
    }

    public PedalAxis SelectedAxis
    {
        get => selectedAxis;
        set
        {
            if (GraphPedal != "All pedals") { GraphPedal = value.ToString(); }
            if (selectedAxis == value) { return; }
            engine.RevokeRelease();
            selectedAxis = value;
            CancelCalibration();
            Changed();
            Changed(nameof(Editor));
            NotifyFilterSettings();
            Changed(nameof(AxisTitle));
            UpdateCurve();
            RefreshDraftCurve();
            Refresh();
        }
    }

    public AxisEditor Editor => editors[selectedAxis];
    public string AxisTitle => SelectedAxis + " processing";
    public string ActiveProfileInfo => "Applied: " + applied.Name;
    private string sourceName = "No source selected";
    public string SourceName { get => sourceName; set => Set(ref sourceName, value); }
    private string status = "Not connected";
    public string Status { get => status; private set => Set(ref status, value); }
    private bool isReceiving;
    public bool IsReceiving { get => isReceiving; private set => Set(ref isReceiving, value); }
    private string sampleInfo = "Waiting for live pedal input";
    public string SampleInfo { get => sampleInfo; private set => Set(ref sampleInfo, value); }
    private string observedRange = "—";
    public string ObservedRange { get => observedRange; private set => Set(ref observedRange, value); }
    private string correctionInfo = "Applied 0 · target 0";
    public string CorrectionInfo { get => correctionInfo; private set => Set(ref correctionInfo, value); }
    private string resetInfo = "No session";
    public string ResetInfo { get => resetInfo; private set => Set(ref resetInfo, value); }
    public string Message { get; private set; } = "Tuning changes apply automatically. Save the profile to keep them after restart.";
    private string hardwareInfo = "Not read. Use Read hardware calibration to inspect the stored bounds.";
    public string HardwareInfo { get => hardwareInfo; set => Set(ref hardwareInfo, value); }
    public IReadOnlyList<PlotSample> History { get; private set; } = [];
    public IReadOnlyList<PlotSample> Curve { get; private set; } = [];

    public void Refresh(bool force = false)
    {
        if (IsSwitchingMode && !force) { return; }
        RefreshDrift();
        var frame = engine.Snapshot(selectedAxis);
        InputConnection = frame.Connection;
        RefreshCalibration();
        var sensorCounts = engine.DriftState(selectedAxis).RawAvailable;
        foreach (var card in Cards)
        {
            if (IsCalibrationMode)
            {
                var input = HardwareDisplay?.CurrentAxes;
                var count = input.HasValue ? card.Axis switch
                { PedalAxis.Throttle => input.Value.Throttle, PedalAxis.Brake => input.Value.Brake, _ => input.Value.Clutch } : (int?)null;
                card.UpdateControllerInput(count, HardwareDisplay?.SensorChannel?.ToString() == card.Axis.ToString());
            }
            else { card.Update(frame.Axes.Single(axis => axis.Axis == card.Axis), sensorCounts); }
        }

        var selected = frame.Axes.Single(axis => axis.Axis == selectedAxis);
        IsReceiving = selected.IsFresh;
        Status = selected.IsFresh ? "Connected" : "Not connected";
        if (IsCalibrationMode)
        { Status = HardwareDisplay?.CurrentAxes is not null ? "Connected" : "Not connected"; }
        if (frame.Detail?.StartsWith("Source error:", StringComparison.Ordinal) == true)
        {
            ShowMessage(frame.Detail);
        }
        SampleInfo = FormattableString.Invariant($"{frame.SampleCount:N0} reports · {frame.ReportsPerSecond:F0} source Hz · {frame.ElapsedSeconds:F1} s · connection {frame.Connection}");
        ObservedRange = selected.ObservedMinimum.HasValue
            ? FormattableString.Invariant($"{selected.ObservedMinimum:N0}  →  {selected.ObservedMaximum:N0}") : "—";
        CorrectionInfo = selected.Result is { } result
            ? FormattableString.Invariant($"Applied {result.Processing.BaselineCorrection:F2} · target {result.TargetCorrection:F2}") : "Applied 0 · target 0";
        ResetInfo = selected.Result is { } current
            ? $"{(current.CorrectionSuspended ? "Pending · release permission required" : "Manual baseline only")} · boundary: {current.ResetReason}"
            : "No session";
        History = frame.History;
        Changed(nameof(History));
        if (!GraphPaused)
        {
            PedalHistories = frame.Axes.Select(axis => new PedalHistory(axis.Axis, axis.History ?? [], FilterLabel(applied.Axes[axis.Axis]))).ToArray();
            Changed(nameof(PedalHistories));
        }
        NotifyDisplay();
    }

    private void NotifyDisplay()
    {
        foreach (var name in new[] { nameof(DisplayGraphMode), nameof(DisplayGraphLegend), nameof(DisplayHistories), nameof(DisplaySampleInfo), nameof(GraphPaused), nameof(GraphPauseText) }) { Changed(name); }
    }

    public void Apply()
    {
        var (profile, limits) = ValidateEditors();
        ApplyConfiguration(profile, limits);
        ValidationMessage = "";
        RefreshUnsavedState();
    }

    private void ApplyConfiguration(PedalProfile profile, IReadOnlyDictionary<PedalAxis, AxisSessionSettings> limits)
    {
        if (!IsCalibrating && ProfileJson.Serialize(profile).AsSpan().SequenceEqual(ProfileJson.Serialize(applied))) { return; }
        engine.Configure(profile, limits);
        applied = profile;
        UpdateCurve();
        RefreshDraftCurve();
        Changed(nameof(ActiveProfileInfo));
        NotifyFilterSettings();
    }

    public void ShowMessage(string message)
    {
        if (Message == message) { return; }
        Message = message;
        Changed(nameof(Message));
    }


}
