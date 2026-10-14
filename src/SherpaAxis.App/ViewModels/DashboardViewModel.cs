using System.Collections.ObjectModel;
using System.Globalization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly DashboardEngine engine;
    private readonly Dictionary<PedalAxis, AxisEditor> editors = [];
    private PedalProfile applied = DashboardEngine.DefaultProfile();
    private PedalAxis selectedAxis = PedalAxis.Brake;
    private string profileName = "Device-axis preview";
    private bool plotCounts;
    private ProfileTab? selectedProfile;
    private bool isCalibrating;
    public bool IsCalibrating => isCalibrating;
    public string CalibrationButtonText => IsCalibrating ? "Done" : "Calibrate software";

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
    public static IReadOnlyList<string> CurveKinds { get; } = [.. CurvePresets.Names, "Custom"];
    public IReadOnlyList<PedalCurveViewModel> PedalCurves { get; } = Enum.GetValues<PedalAxis>().Select(axis => new PedalCurveViewModel(axis)).ToArray();
    public static IReadOnlyList<FilterChoice> FilterKinds { get; } =
    [
        new("Off", "Off / bypass"),
        new("Exponential", "Exponential"), new("TwoStageExponential", "Two-stage exponential"),
        new("MovingAverage", "Moving average"), new("Median", "Median"),
        new("WeightedMovingAverage", "Weighted moving average"), new("MedianExponential", "Median + exponential")
    ];
    public ObservableCollection<ProfileTab> ProfileTabs { get; } = [];
    public ProfileTab? SelectedProfile => selectedProfile;
    private string? autoLoadPath;
    public bool SelectedAutoLoad => selectedProfile?.FilePath is { } path && string.Equals(path, autoLoadPath, StringComparison.OrdinalIgnoreCase);
    public void SetAutoLoadPath(string? path)
    {
        autoLoadPath = path;
        foreach (var tab in ProfileTabs) { tab.AutoLoad = tab.FilePath is { } file && string.Equals(file, path, StringComparison.OrdinalIgnoreCase); }
        Changed(nameof(SelectedAutoLoad));
    }
    public ProfileTab AddSavedProfile(PedalProfile profile, string path)
    {
        var tab = new ProfileTab { Name = profile.Name, FilePath = path };
        var defaults = DashboardEngine.DefaultProfile();
        foreach (var axis in AxisNames) { tab.Editors[axis] = AxisEditor.FromProfile(profile.Axes.TryGetValue(axis, out var settings) ? settings : defaults.Axes[axis]); }
        ProfileTabs.Add(tab);
        MarkSaved(tab);
        return tab;
    }
    public IReadOnlyList<CurvePoint> DraftPoints { get; private set; } = [];
    public IReadOnlyList<PlotSample> DraftCurve { get; private set; } = [];
    public string CalibrationInfo { get; private set; } = "Start input, then Calibrate software. Done maps the captured controller output to 0–100% for this profile.";
    public string CalibrationRangeInfo { get; private set; } = "";
    private bool canEdit = true;
    public bool CanEdit { get => canEdit; set { if (Set(ref canEdit, value)) { Changed(nameof(CanTune)); } } }
    private SidePanelMode panelMode;
    private bool isSwitchingMode;
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
    public int InputConnection { get; private set; }
    public bool IsSwitchingMode { get => isSwitchingMode; set { if (Set(ref isSwitchingMode, value)) { Changed(nameof(CanTune)); } } }
    public bool CanTune => CanEdit && !IsCalibrationMode && !IsSwitchingMode;
    public HardwarePedalViewModel? HardwareDisplay { get => hardwareDisplay; set { if (Set(ref hardwareDisplay, value)) { value?.SetGraphWindow(GraphWindow); NotifyDisplay(); } } }
    public string DisplayGraphMode => IsCalibrationMode ? "Device counts" : GraphMode;
    public string DisplayGraphLegend => IsCalibrationMode ? HardwareDisplay?.GraphLegend ?? "Calibration · connect a controller to view live counts" : GraphLegend;
    public IReadOnlyList<PedalHistory> DisplayHistories => IsCalibrationMode ? HardwareDisplay?.Histories ?? HardwarePedalViewModel.EmptyHistories : PedalHistories;
    public string DisplaySampleInfo => IsCalibrationMode ? HardwareDisplay?.StateInfo ?? "Waiting for controller" : SampleInfo;
    private string outputInfo = "Waiting for live input";
    public string OutputInfo { get => outputInfo; set => Set(ref outputInfo, value); }
    public bool PlotCounts
    {
        get => plotCounts;
        set { plotCounts = value; GraphMode = value ? "Device counts" : "Input vs filtered"; Changed(); }
    }

    public static IReadOnlyList<string> GraphPedals { get; } = ["All pedals", "Throttle", "Brake", "Clutch"];
    public static IReadOnlyList<string> GraphModes { get; } = ["Input vs filtered", "Filtered vs curved", "Output", "Input", "Filtered", "Filter difference", "Device counts"];
    public static IReadOnlyList<string> FilterTimes { get; } = ["1", "3", "5", "8", "10", "15", "20", "30", "50"];
    public static IReadOnlyList<string> FilterWindows { get; } = ["3", "5", "7", "9", "11", "15", "21", "31"];
    public static IReadOnlyList<string> ResetGaps { get; } = ["50", "100", "200", "500", "1000"];
    public static IReadOnlyList<string> FilterPresetNames => FilterPresets.Names;
    private string graphPedal = "All pedals";
    private string graphMode = "Input vs filtered";
    private bool graphZoom;
    private double graphWindow = 10;
    private bool graphPaused;
    public bool GraphPaused
    {
        get => IsCalibrationMode && HardwareDisplay is not null ? HardwareDisplay.GraphPaused : graphPaused;
        set
        {
            if (IsCalibrationMode && HardwareDisplay is not null) { HardwareDisplay.GraphPaused = value; Changed(); Changed(nameof(GraphPauseText)); }
            else if (Set(ref graphPaused, value)) { Changed(nameof(GraphPauseText)); }
        }
    }
    public string GraphPauseText => GraphPaused ? "Resume graph" : "Pause graph";
    public string GraphPedal { get => graphPedal; set => Set(ref graphPedal, value); }
    public string GraphMode { get => graphMode; set { if (Set(ref graphMode, value)) { Changed(nameof(GraphLegend)); NotifyDisplay(); } } }
    public string GraphLegend => GraphMode switch
    {
        "Input vs filtered" => "Dashed: input · Color: filtered · Before curve/deadzones · Applied settings",
        "Filtered vs curved" => "Dashed: filtered input · Color: output after deadzones and curve · Applied settings",
        "Filter difference" => "Input minus filtered · Auto-scaled in percentage points (pp) · Before curve/deadzones",
        "Device counts" => "Original device counts · Each pedal has its own scale · Applied settings",
        "Output" => "Final output after filter, deadzones and curve · Applied settings",
        _ => "Normalized signal before deadzones and curve · Applied settings"
    };
    public bool GraphZoom { get => graphZoom; set => Set(ref graphZoom, value); }
    public double GraphWindow
    {
        get => graphWindow;
        set
        {
            if (!double.IsFinite(value) || value is < .1 or > 60) { throw new ArgumentOutOfRangeException(nameof(value), "Enter a time window from 0.1 to 60 seconds."); }
            if (Set(ref graphWindow, value)) { engine.SetGraphWindow(value); HardwareDisplay?.SetGraphWindow(value); }
        }
    }
    public bool HasFilterDraft
    {
        get
        {
            try
            {
                var draft = Editor.BuildProfile().Filter;
                var current = applied.Axes[SelectedAxis].Filter;
                return draft is null ? current is not null : current is null || draft.Kind != current.Kind
                    || draft.TimeConstantSeconds != current.TimeConstantSeconds || draft.WindowSamples != current.WindowSamples
                    || draft.ResetAfterSeconds != current.ResetAfterSeconds;
            }
            catch (ArgumentException) { return true; }
        }
    }
    public string FilterApplyInfo => HasFilterDraft ? "Filter draft · Apply settings to update" : "Filter settings applied";
    public IReadOnlyList<PedalHistory> PedalHistories { get; private set; } = [];
    public string FilterPreset => FilterPresets.Identify(Editor);
    public string SelectedFilterKind => Editor.FilterEnabled ? Editor.FilterKind : "Off";
    public bool UsesWindowFilter => Editor.FilterKind is "MovingAverage" or "Median" or "WeightedMovingAverage" or "MedianExponential";
    public bool UsesTimeFilter => Editor.FilterKind is "Exponential" or "TwoStageExponential" or "MedianExponential";
    public string FilterInfo => !Editor.FilterEnabled ? "Filtering is off. Choose Sharp, Balanced or Smooth, or select an algorithm to enable it." : Editor.FilterKind switch
    {
        "Median" => "Removes isolated spikes. Larger windows reject longer spikes and add delay.",
        "MovingAverage" => "Averages recent reports. Larger windows smooth more and react more slowly.",
        "WeightedMovingAverage" => "Averages recent reports with more weight on the newest samples.",
        "MedianExponential" => "Rejects spikes first, then smooths the result. Uses both window and time constant.",
        "TwoStageExponential" => "Two smoothing stages. Smooth transitions with more delay than one stage.",
        _ => "One smoothing stage. A larger time constant reduces noise and slows response."
    };

    public void SetFilterPreset(string name)
    {
        if (name == "Custom") { NotifyFilterSettings(); return; }
        var settings = FilterPresets.Create(name);
        Editor.FilterMilliseconds = ((settings?.TimeConstantSeconds ?? 0) * 1000).ToString("R", CultureInfo.InvariantCulture);
        if (settings is not null)
        {
            Editor.FilterKind = settings.Kind.ToString();
            Editor.FilterWindowSamples = settings.WindowSamples.ToString(CultureInfo.InvariantCulture);
            Editor.FilterGapMilliseconds = (settings.ResetAfterSeconds * 1000).ToString("R", CultureInfo.InvariantCulture);
        }
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage($"{SelectedAxis}: {name} filter in draft. Apply settings to see it in the graph.");
    }

    public void NotifyFilterSettings()
    {
        Changed(nameof(FilterPreset));
        Changed(nameof(SelectedFilterKind));
        Changed(nameof(UsesWindowFilter));
        Changed(nameof(UsesTimeFilter));
        Changed(nameof(FilterInfo));
        Changed(nameof(HasFilterDraft)); Changed(nameof(FilterApplyInfo));
        Changed(nameof(SelectedCorrectionMode)); Changed(nameof(IsButtonCorrection)); Changed(nameof(CorrectionModeHint)); Changed(nameof(CorrectionBindingLabel));
        Changed(nameof(SelectedAutoLoad));
    }

    public static IReadOnlyList<string> CorrectionModes { get; } = ["Off", "Auto", "By button"];
    public string SelectedCorrectionMode => Editor.CorrectionMode switch { BaselineCorrectionMode.Auto => "Auto", BaselineCorrectionMode.ByButton => "By button", _ => "Off" };
    public bool IsButtonCorrection => Editor.CorrectionMode == BaselineCorrectionMode.ByButton;
    public string CorrectionModeHint => Editor.CorrectionMode switch
    {
        BaselineCorrectionMode.Auto => "Captures one stable near-zero baseline after a press and release. Limited to 0.5% of the axis range; active pedal travel is excluded. Hardware-clipped values cannot be recovered.",
        BaselineCorrectionMode.ByButton => "Hold the bound key or controller button only while this pedal is fully released. Adjustment is bounded and stops when you release the button.",
        _ => "Baseline adjustment is off. Hardware calibration remains unchanged."
    };
    public string CorrectionBindingLabel => Editor.CorrectionButton is not { } button ? "No button bound" : button.Kind == "keyboard"
        ? "Key: " + System.Windows.Input.KeyInterop.KeyFromVirtualKey(button.Code) : $"Controller button {button.Code}";
    public void SetCorrectionMode(string mode)
    {
        var selected = mode switch
        {
            "Off" => BaselineCorrectionMode.Off,
            "Auto" => BaselineCorrectionMode.Auto,
            "By button" => BaselineCorrectionMode.ByButton,
            _ => throw new ArgumentException("Choose a supported correction mode.", nameof(mode))
        };
        Editor.CorrectionMode = selected;
        if (selected == BaselineCorrectionMode.Off) { Editor.MaximumCorrection = "0"; }
        else if (Editor.BuildProfile().MaximumBaselineCorrection == 0)
        {
            var span = Editor.BuildProfile().Calibration.Span;
            Editor.MaximumCorrection = (span * .005).ToString("R", CultureInfo.InvariantCulture);
            Editor.CorrectionRate = (span * .1).ToString("R", CultureInfo.InvariantCulture);
        }
        NotifyFilterSettings(); Apply();
    }
    public void BindCorrection(CorrectionButton button)
    { Editor.CorrectionButton = button; NotifyFilterSettings(); Apply(); }

    public void SetFilterKind(string kind)
    {
        if (kind == "Off") { SetFilterPreset("Off"); return; }
        if (!FilterKinds.Any(choice => choice.Kind == kind)) { throw new ArgumentException("Choose a supported filter.", nameof(kind)); }
        Editor.FilterKind = kind;
        Editor.FilterEnabled = true;
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage($"{SelectedAxis}: filter changed in draft. Apply settings to update processing.");
    }

    private static string FilterLabel(AxisProfile axis)
    {
        if (axis.Filter is not { } f) { return "Filter off"; }
        var name = FilterKinds.Single(choice => choice.Kind == f.Kind.ToString()).Name;
        var window = f.Kind is SignalFilterKind.Median or SignalFilterKind.MovingAverage or SignalFilterKind.WeightedMovingAverage or SignalFilterKind.MedianExponential;
        var time = f.Kind is SignalFilterKind.Exponential or SignalFilterKind.TwoStageExponential or SignalFilterKind.MedianExponential;
        return name + (window ? $" · {f.WindowSamples} samples" : "") + (time ? FormattableString.Invariant($" · {f.TimeConstantSeconds * 1000:G} ms") : "");
    }

    public string ProfileName
    {
        get => profileName;
        set { if (profileName == value) { return; } profileName = value; if (selectedProfile is not null) { selectedProfile.Name = value; } Changed(); }
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
    public string Message { get; private set; } = "Device-axis units · hardware calibration is separate · edits take effect with Apply";
    private string hardwareInfo = "Not read. Use Read hardware calibration to inspect the stored bounds.";
    public string HardwareInfo { get => hardwareInfo; set => Set(ref hardwareInfo, value); }
    public IReadOnlyList<PlotSample> History { get; private set; } = [];
    public IReadOnlyList<PlotSample> Curve { get; private set; } = [];

    public void Refresh(bool force = false)
    {
        if (IsSwitchingMode && !force) { return; }
        var frame = engine.Snapshot(selectedAxis);
        InputConnection = frame.Connection;
        RefreshCalibration();
        foreach (var card in Cards)
        {
            if (IsCalibrationMode)
            {
                var input = HardwareDisplay?.CurrentAxes;
                var count = input.HasValue ? card.Axis switch
                { PedalAxis.Throttle => input.Value.Throttle, PedalAxis.Brake => input.Value.Brake, _ => input.Value.Clutch } : (int?)null;
                card.UpdateControllerInput(count, HardwareDisplay?.SensorChannel?.ToString() == card.Axis.ToString());
            }
            else { card.Update(frame.Axes.Single(axis => axis.Axis == card.Axis)); }
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

    public (PedalProfile Profile, IReadOnlyDictionary<PedalAxis, AxisSessionSettings> Limits) ValidateEditors()
    {
        var profiles = new Dictionary<PedalAxis, AxisProfile>();
        var limits = new Dictionary<PedalAxis, AxisSessionSettings>();
        foreach (var (axis, editor) in editors)
        {
            try
            {
                profiles.Add(axis, editor.BuildProfile());
                limits.Add(axis, editor.BuildSessionSettings());
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"{axis}: {exception.Message}", exception);
            }
        }

        return (new(ProfileName, profiles), limits);
    }

    public void Apply()
    {
        var (profile, limits) = ValidateEditors();
        engine.Configure(profile, limits);
        applied = profile;
        if (selectedProfile is not null) { selectedProfile.RequiresApply = false; }
        UpdateCurve();
        RefreshDraftCurve();
        Changed(nameof(ActiveProfileInfo));
        NotifyFilterSettings();
        RefreshUnsavedState();
        ShowMessage("Settings applied. Correction and filter histories reset; vJoy resumes with fresh pedal input.");
    }

    public void Load(PedalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        StoreDraft();
        CancelCalibration();
        selectedProfile = new ProfileTab { Name = profile.Name };
        ProfileTabs.Add(selectedProfile);
        var defaults = DashboardEngine.DefaultProfile();
        foreach (var axis in AxisNames)
        {
            editors[axis] = AxisEditor.FromProfile(profile.Axes.TryGetValue(axis, out var value) ? value : defaults.Axes[axis]);
        }

        ProfileName = profile.Name;
        Apply();
        RefreshDraftCurve();
        Changed(nameof(SelectedProfile));
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage("Profile loaded. Missing axes use device-range defaults. Check that calibration matches this input source.");
    }

    public void CaptureEndpoint(bool maximum)
    {
        var frame = engine.Snapshot(selectedAxis).Axes.Single(axis => axis.Axis == selectedAxis);
        if (!frame.IsFresh || frame.Result is null)
        {
            throw new InvalidOperationException("A fresh sample is required. Start live input or playback first.");
        }

        var value = frame.Result.Value.Processing.Input.ToString("R", CultureInfo.InvariantCulture);
        editors[selectedAxis] = maximum ? Editor with { Maximum = value } : Editor with { Minimum = value };
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage("Captured into the editor. Review the bounds, then Apply.");
    }

    public void UseObservedRange()
    {
        var frame = engine.Snapshot(selectedAxis).Axes.Single(axis => axis.Axis == selectedAxis);
        if (!frame.ObservedMinimum.HasValue || frame.ObservedMaximum <= frame.ObservedMinimum)
        {
            throw new InvalidOperationException("Move the selected pedal through its range first; minimum and maximum must differ.");
        }

        editors[selectedAxis] = Editor with
        {
            Minimum = frame.ObservedMinimum.Value.ToString("R", CultureInfo.InvariantCulture),
            Maximum = frame.ObservedMaximum!.Value.ToString("R", CultureInfo.InvariantCulture)
        };
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage("Observed bounds copied into the editor. Review them, then Apply.");
    }

    public void ResetEditor()
    {
        CancelCalibration();
        editors[selectedAxis] = AxisEditor.FromProfile(DashboardEngine.DefaultProfile().Axes[selectedAxis]);
        Changed(nameof(Editor));
        NotifyFilterSettings();
        RefreshDraftCurve();
        ShowMessage("Device-range defaults are in the editor. Apply to activate them.");
    }

    public void ResetInputMapping()
    {
        CancelCalibration();
        editors[selectedAxis] = Editor with
        {
            Minimum = "0",
            Maximum = DashboardEngine.DeviceMaximum(selectedAxis).ToString("R", CultureInfo.InvariantCulture),
            Inverted = false
        };
        Changed(nameof(Editor));
        ShowMessage("Full controller range is in the draft. Apply settings to activate. Curves, filters and deadzones are retained.");
    }

    public void ShowMessage(string message)
    {
        if (Message == message) { return; }
        Message = message;
        Changed(nameof(Message));
    }

    public void SwitchProfile(ProfileTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab == selectedProfile || !ProfileTabs.Contains(tab)) { return; }
        StoreDraft();
        engine.RevokeRelease();
        engine.SuspendOutput("profile switched");
        CancelCalibration();
        selectedProfile = tab;
        foreach (var (axis, editor) in tab.Editors) { editors[axis] = editor with { }; }
        ProfileName = tab.Name;
        try
        {
            if (!tab.RequiresApply) { Apply(); }
            else { ShowMessage("Recovered draft selected. Review it and Apply settings to activate it."); }
        }
        catch (ArgumentException exception) { ShowMessage($"Draft restored; previous processing remains active until Apply: {exception.Message}"); }
        RefreshDraftCurve();
        Changed(nameof(SelectedProfile));
        Changed(nameof(Editor));
        NotifyFilterSettings();
        Refresh();
        RefreshUnsavedState();
    }

    private bool hasRecoverableDrafts;
    public bool HasRecoverableDrafts { get => hasRecoverableDrafts; set => Set(ref hasRecoverableDrafts, value); }
    private bool canUndoDelete;
    public bool CanUndoDelete { get => canUndoDelete; set => Set(ref canUndoDelete, value); }
    private string draftStatus = "";
    public string DraftStatus { get => draftStatus; private set => Set(ref draftStatus, value); }

    public void RefreshUnsavedState()
    {
        StoreDraft();
        foreach (var tab in ProfileTabs)
        {
            var changed = tab.Name != tab.SavedName || tab.Editors.Count != tab.SavedEditors.Count
                || tab.Editors.Any(pair => !tab.SavedEditors.TryGetValue(pair.Key, out var saved) || pair.Value != saved);
            tab.HasUnsavedChanges = changed || tab.FilePath is null && tab.Recoverable;
            if (changed) { tab.Recoverable = true; }
        }
        var pending = true;
        try { pending = !ProfileJson.Serialize(ValidateEditors().Profile).AsSpan().SequenceEqual(ProfileJson.Serialize(applied)); }
        catch (ArgumentException) { }
        DraftStatus = pending ? "Unapplied edits · Apply settings to activate"
            : selectedProfile?.HasUnsavedChanges == true ? "Unsaved profile · Save to keep these settings"
            : selectedProfile?.FilePath is null ? "Default settings · Save to create a profile" : "Profile saved · Settings applied";
    }

    public void MarkSelectedSaved()
    {
        StoreDraft();
        if (selectedProfile is not null) { MarkSaved(selectedProfile); }
        RefreshUnsavedState();
    }
    private static void MarkSaved(ProfileTab tab)
    {
        tab.SavedName = tab.Name;
        tab.SavedEditors.Clear();
        foreach (var (axis, editor) in tab.Editors) { tab.SavedEditors[axis] = editor with { }; }
        tab.HasUnsavedChanges = false;
    }
    public ProfileDraftSession CaptureDrafts()
    {
        RefreshUnsavedState();
        return new(1, ProfileTabs.Where(tab => tab.HasUnsavedChanges && tab.Recoverable).Select(tab => new ProfileDraft(tab.DraftId,
            tab.Name, tab.FilePath is null ? null : System.IO.Path.GetFileName(tab.FilePath), tab.Editors.ToDictionary(pair => pair.Key, pair => pair.Value with { }))).ToArray());
    }
    public void RestoreDrafts(ProfileDraftSession session, string libraryDirectory)
    {
        RefreshUnsavedState();
        var restored = new List<ProfileTab>();
        foreach (var draft in session.Profiles)
        {
            // Keep the saved version intact; restored editors always require explicit Apply.
            var tab = new ProfileTab { DraftId = draft.Id, Name = draft.Name, RequiresApply = true };
            foreach (var (axis, editor) in draft.Editors) { tab.Editors[axis] = editor with { }; }
            if (draft.SavedFileName is { } name)
            {
                var path = System.IO.Path.Combine(libraryDirectory, name);
                var saved = ProfileTabs.FirstOrDefault(candidate => string.Equals(candidate.FilePath, path, StringComparison.OrdinalIgnoreCase));
                if (saved is not null && !saved.HasUnsavedChanges)
                {
                    tab.FilePath = saved.FilePath; tab.SavedName = saved.SavedName;
                    foreach (var (axis, editor) in saved.SavedEditors) { tab.SavedEditors[axis] = editor with { }; }
                    ProfileTabs.Remove(saved);
                }
            }
            ProfileTabs.Add(tab); restored.Add(tab);
        }
        if (restored.Count > 0) { SwitchProfile(restored[0]); }
        HasRecoverableDrafts = false;
        RefreshUnsavedState();
        ShowMessage("Unsaved drafts recovered. Review them and Apply settings to activate; Save keeps them as profiles.");
    }

    public ProfileTab DetachSelectedProfile()
    {
        StoreDraft();
        var removed = selectedProfile ?? throw new InvalidOperationException("Select a profile first.");
        RemoveSelectedProfile();
        return removed;
    }
    public void RestoreDeletedProfile(ProfileTab tab, int index)
    {
        tab.RequiresApply = true;
        ProfileTabs.Insert(Math.Clamp(index, 0, ProfileTabs.Count), tab);
        SwitchProfile(tab);
    }

    public void RemoveSelectedProfile()
    {
        if (selectedProfile is not { } removed) { return; }
        var index = ProfileTabs.IndexOf(removed);
        var next = ProfileTabs.Skip(index + 1).Concat(ProfileTabs.Take(index).Reverse()).FirstOrDefault(CanApply);
        if (next is not null) { SwitchProfile(next); }
        else
        {
            Load(DashboardEngine.DefaultProfile());
            selectedProfile!.Recoverable = false;
            MarkSelectedSaved();
        }
        ProfileTabs.Remove(removed);

        static bool CanApply(ProfileTab tab)
        {
            try
            {
                _ = new PedalProfile(tab.Name, tab.Editors.ToDictionary(pair => pair.Key, pair => pair.Value.BuildProfile()));
                return true;
            }
            catch (ArgumentException) { return false; }
        }
    }

    public void RefreshDraftCurve()
    {
        foreach (var panel in PedalCurves) { panel.Refresh(editors[panel.Axis], AppliedCurve(panel.Axis)); }
        try
        {
            var curve = Editor.BuildCurve();
            DraftPoints = CurvePresets.Handles(curve);
            var processor = new AxisProfile(new(0, 1), curve: curve).CreateProcessor();
            DraftCurve = Enumerable.Range(0, 201).Select(i => new PlotSample(i / 200d, 0, 0, processor.Process(i / 200d).Output)).ToArray();
        }
        catch (ArgumentException) { DraftPoints = []; DraftCurve = []; }
        Changed(nameof(DraftPoints));
        Changed(nameof(DraftCurve));
    }

    public void EditCurve(IEnumerable<CurvePoint> points)
        => EditCurve(selectedAxis, points);

    public void EditCurve(PedalAxis axis, IEnumerable<CurvePoint> points)
    {
        editors[axis].SetCurvePoints(points);
        RefreshDraftCurve();
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage($"{axis} curve edited. Apply activates it; Save profile keeps it for later.");
    }

    public void SetCurvePreset(PedalAxis axis, string name)
    {
        if (!CurveKinds.Contains(name)) { throw new ArgumentException("Unknown curve preset.", nameof(name)); }
        editors[axis].CurveKind = name;
        RefreshDraftCurve();
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage($"{axis}: {name} curve ready. Apply activates all pedal drafts.");
    }

    public void ClearCurve(PedalAxis axis)
    {
        editors[axis].CurveKind = "Linear";
        editors[axis].Exponent = "1";
        editors[axis].Points = "0:0; 1:1";
        RefreshDraftCurve();
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage($"{axis} curve cleared to linear. Calibration, deadzones and filters retained. Apply to activate.");
    }

    public void CancelCalibration()
    {
        engine.CancelCalibration();
        SetCalibrating(false);
        CalibrationInfo = "Start input, then Calibrate software. Done maps the captured controller output to 0–100% for this profile.";
        CalibrationRangeInfo = "";
        Changed(nameof(CalibrationInfo));
        Changed(nameof(CalibrationRangeInfo));
    }

    public void AdvanceCalibration()
    {
        try
        {
            if (!IsCalibrating)
            {
                engine.StartCalibration(selectedAxis);
                SetCalibrating(true);
                CalibrationInfo = $"Release and press {SelectedAxis} through its full desired range, then click Done. All input reports are captured.";
                RefreshCalibration();
            }
            else
            {
                applied = engine.CompleteCalibration(selectedAxis);
                var result = applied.Axes[selectedAxis].Calibration;
                editors[selectedAxis] = Editor with
                {
                    Minimum = result.Minimum.ToString("R", CultureInfo.InvariantCulture),
                    Maximum = result.Maximum.ToString("R", CultureInfo.InvariantCulture),
                    Inverted = false
                };
                SetCalibrating(false);
                CalibrationInfo = "Software mapping applied. Minimum = 0%, maximum = 100%. Save profile to keep it after restart. Other draft settings still require Apply.";
                CalibrationRangeInfo = FormattableString.Invariant($"0% = {result.Minimum:N0}\n100% = {result.Maximum:N0}");
                Changed(nameof(CalibrationRangeInfo));
                Changed(nameof(Editor));
                NotifyFilterSettings();
                UpdateCurve();
                RefreshDraftCurve();
                Refresh();
                ShowMessage($"{SelectedAxis} calibration applied immediately. Virtual output stays disarmed; Save profile to keep these bounds.");
            }
            Changed(nameof(CalibrationInfo));
        }
        catch (InvalidOperationException exception)
        {
            CalibrationInfo = exception.Message;
            Changed(nameof(CalibrationInfo));
            RefreshCalibration();
            throw;
        }
    }

    private void SetCalibrating(bool value)
    {
        isCalibrating = value;
        Changed(nameof(IsCalibrating));
        Changed(nameof(CalibrationButtonText));
    }

    private void RefreshCalibration()
    {
        if (!IsCalibrating) { return; }
        var capture = engine.CalibrationSnapshot();
        if (capture is not { IsActive: true } || capture.Axis != selectedAxis)
        {
            SetCalibrating(false);
            CalibrationInfo = capture?.Failure ?? "Software calibration cancelled. Click Calibrate software to start again.";
            CalibrationRangeInfo = "";
            Changed(nameof(CalibrationInfo));
            Changed(nameof(CalibrationRangeInfo));
            ShowMessage(CalibrationInfo);
            return;
        }
        var range = capture.SampleCount == 0 ? "Waiting for the first input report…"
            : FormattableString.Invariant($"0% = {capture.Minimum:N0}\n100% = {capture.Maximum:N0}\nInput: {capture.Current:N0}");
        if (CalibrationRangeInfo != range)
        {
            CalibrationRangeInfo = range;
            Changed(nameof(CalibrationRangeInfo));
        }
    }

    private void StoreDraft()
    {
        if (selectedProfile is null) { return; }
        selectedProfile.Name = ProfileName;
        selectedProfile.Editors.Clear();
        foreach (var (axis, editor) in editors) { selectedProfile.Editors.Add(axis, editor with { }); }
    }

    private void UpdateCurve()
    {
        Curve = AppliedCurve(selectedAxis);
        Changed(nameof(Curve));
    }

    private PlotSample[] AppliedCurve(PedalAxis axis)
    {
        var profile = applied.Axes[axis];
        var processor = new AxisProfile(profile.Calibration, profile.Deadzone, profile.Curve).CreateProcessor();
        return Enumerable.Range(0, 201).Select(index =>
        {
            var fraction = index / 200d;
            var input = profile.Calibration.Inverted ? profile.Calibration.Maximum - fraction * profile.Calibration.Span
                : profile.Calibration.Minimum + fraction * profile.Calibration.Span;
            input = Math.Clamp(input, profile.Calibration.Minimum, profile.Calibration.Maximum);
            return new PlotSample(fraction, fraction, fraction, processor.Process(input).Output);
        }).ToArray();
    }
}
