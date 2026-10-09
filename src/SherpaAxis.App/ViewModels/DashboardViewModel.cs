using System.Collections.ObjectModel;
using System.Globalization;
using SherpaAxis.App.Models;
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
    public string CalibrationButtonText => IsCalibrating ? "Done" : "Calibrate";

    public DashboardViewModel(DashboardEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.engine = engine;
        Load(applied);
        ShowMessage("Start live input or open a capture. All editable settings affect the software preview; hardware calibration is separate.");
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
    public IReadOnlyList<CurvePoint> DraftPoints { get; private set; } = [];
    public IReadOnlyList<PlotSample> DraftCurve { get; private set; } = [];
    public string CalibrationInfo { get; private set; } = "Start input, then Calibrate. Release and press this pedal through its range; Done applies the captured bounds.";
    public string CalibrationRangeInfo { get; private set; } = "";
    public static IReadOnlyList<double> PlaybackSpeeds { get; } = [1, 5, 20];
    public double PlaybackSpeed { get; set; } = 1;
    private bool canEdit = true;
    public bool CanEdit { get => canEdit; set => Set(ref canEdit, value); }
    private bool canRecord;
    public bool CanRecord { get => canRecord; set => Set(ref canRecord, value); }
    private bool canFinishRecording;
    public bool CanFinishRecording { get => canFinishRecording; set => Set(ref canFinishRecording, value); }
    private string recordingInfo = "Not recording";
    public string RecordingInfo { get => recordingInfo; set => Set(ref recordingInfo, value); }
    private string outputInfo = "Output disabled";
    public string OutputInfo { get => outputInfo; set => Set(ref outputInfo, value); }
    private string vJoyPath = Bridge.VJoyController.DefaultLibraryPath;
    private string vJoyDevice = "1";
    public string VJoyPath { get => vJoyPath; set => Set(ref vJoyPath, value); }
    public string VJoyDevice { get => vJoyDevice; set => Set(ref vJoyDevice, value); }
    public bool PlotCounts
    {
        get => plotCounts;
        set { plotCounts = value; GraphMode = value ? "Device counts" : "Input vs filtered"; Changed(); }
    }

    public static IReadOnlyList<string> GraphPedals { get; } = ["All pedals", "Throttle", "Brake", "Clutch"];
    public static IReadOnlyList<string> GraphModes { get; } = ["Input vs filtered", "Filtered vs curved", "Output", "Input", "Filtered", "Filter difference", "Device counts"];
    public static IReadOnlyList<double> GraphWindows { get; } = [1, 3, 6, 12];
    public static IReadOnlyList<string> FilterTimes { get; } = ["1", "3", "5", "8", "10", "15", "20", "30", "50"];
    public static IReadOnlyList<string> FilterWindows { get; } = ["3", "5", "7", "9", "11", "15", "21", "31"];
    public static IReadOnlyList<string> ResetGaps { get; } = ["50", "100", "200", "500", "1000"];
    public static IReadOnlyList<string> FilterPresetNames => FilterPresets.Names;
    private string graphPedal = "All pedals";
    private string graphMode = "Input vs filtered";
    private bool graphZoom;
    private double graphWindow = 12;
    private bool graphPaused;
    public bool GraphPaused { get => graphPaused; set { if (Set(ref graphPaused, value)) { Changed(nameof(GraphPauseText)); } } }
    public string GraphPauseText => GraphPaused ? "Resume graph" : "Pause graph";
    private string contextHint = "Hover a control for help, or focus it with Tab. Edit a pedal, then Apply settings.";
    public string GraphPedal { get => graphPedal; set => Set(ref graphPedal, value); }
    public string GraphMode { get => graphMode; set { if (Set(ref graphMode, value)) { Changed(nameof(GraphLegend)); } } }
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
    public double GraphWindow { get => graphWindow; set => Set(ref graphWindow, value); }
    public string ContextHint { get => contextHint; set => Set(ref contextHint, value); }
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
    }

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
    private string status = "Stopped";
    public string Status { get => status; private set => Set(ref status, value); }
    private bool isReceiving;
    public bool IsReceiving { get => isReceiving; private set => Set(ref isReceiving, value); }
    private string sampleInfo = "Start live input or open a recorded capture";
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

    public void Refresh()
    {
        var frame = engine.Snapshot(selectedAxis);
        RefreshCalibration();
        foreach (var card in Cards)
        {
            card.Update(frame.Axes.Single(axis => axis.Axis == card.Axis));
        }

        var selected = frame.Axes.Single(axis => axis.Axis == selectedAxis);
        IsReceiving = selected.IsFresh;
        Status = selected.IsFresh ? "Receiving" : frame.State == FeelVR.PedalConnectionState.Connected ? "Waiting for fresh input" : frame.Detail ?? frame.State.ToString();
        if (Status.StartsWith("Source error:", StringComparison.Ordinal))
        {
            ShowMessage(Status);
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
        UpdateCurve();
        RefreshDraftCurve();
        Changed(nameof(ActiveProfileInfo));
        ShowMessage("Applied to software preview. All correction and filter histories were cleared.");
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
        try { Apply(); }
        catch (ArgumentException exception) { ShowMessage($"Draft restored; previous processing remains active until Apply: {exception.Message}"); }
        RefreshDraftCurve();
        Changed(nameof(SelectedProfile));
        Changed(nameof(Editor));
        NotifyFilterSettings();
        Refresh();
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
        CalibrationInfo = "Start input, then Calibrate. Release and press this pedal through its range; Done applies the captured bounds.";
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
                CalibrationInfo = "Calibration applied. Minimum = 0%, maximum = 100%. Save profile to keep it after restart. Other draft settings still require Apply.";
                CalibrationRangeInfo = FormattableString.Invariant($"0% = {result.Minimum:N0} counts · 100% = {result.Maximum:N0} counts");
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
            CalibrationInfo = capture?.Failure ?? "Calibration cancelled. Click Calibrate to start again.";
            CalibrationRangeInfo = "";
            Changed(nameof(CalibrationInfo));
            Changed(nameof(CalibrationRangeInfo));
            ShowMessage(CalibrationInfo);
            return;
        }
        var range = capture.SampleCount == 0 ? "Waiting for the first input report…"
            : FormattableString.Invariant($"0% = {capture.Minimum:N0} counts · 100% = {capture.Maximum:N0} counts\nInput: {capture.Current:N0} · {capture.SampleCount:N0} captured reports");
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
