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
    private CalibrationDraft? calibration;

    public DashboardViewModel(DashboardEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.engine = engine;
        Load(applied);
        ShowMessage("Start live input or open a capture. All editable settings affect the software preview; hardware calibration is separate.");
    }

    public IReadOnlyList<AxisCardViewModel> Cards { get; } = Enum.GetValues<PedalAxis>().Select(axis => new AxisCardViewModel(axis)).ToArray();
    public static IReadOnlyList<PedalAxis> AxisNames { get; } = Enum.GetValues<PedalAxis>();
    public static IReadOnlyList<string> CurveKinds { get; } = [.. CurvePresets.Names, "Power", "Custom"];
    public IReadOnlyList<PedalCurveViewModel> PedalCurves { get; } = Enum.GetValues<PedalAxis>().Select(axis => new PedalCurveViewModel(axis)).ToArray();
    public static IReadOnlyList<FilterChoice> FilterKinds { get; } =
    [
        new("Exponential", "Exponential"), new("TwoStageExponential", "Two-stage exponential"),
        new("MovingAverage", "Moving average"), new("Median", "Median")
    ];
    public ObservableCollection<ProfileTab> ProfileTabs { get; } = [];
    public ProfileTab? SelectedProfile => selectedProfile;
    public IReadOnlyList<CurvePoint> DraftPoints { get; private set; } = [];
    public IReadOnlyList<PlotSample> DraftCurve { get; private set; } = [];
    public string CalibrationInfo { get; private set; } = "Release this pedal, then capture released. Software calibration only.";
    public static IReadOnlyList<double> PlaybackSpeeds { get; } = [1, 5, 20];
    public double PlaybackSpeed { get; set; } = 1;
    public bool CanEdit { get; set; } = true;
    public bool CanRecord { get; set; }
    public bool CanFinishRecording { get; set; }
    public string RecordingInfo { get; set; } = "Not recording";
    public string OutputInfo { get; set; } = "Output disabled";
    public string VJoyPath { get; set; } = Bridge.VJoyController.DefaultLibraryPath;
    public string VJoyDevice { get; set; } = "1";
    public bool PlotCounts
    {
        get => plotCounts;
        set { plotCounts = value; Changed(); }
    }

    public string ProfileName
    {
        get => profileName;
        set { profileName = value; if (selectedProfile is not null) { selectedProfile.Name = value; } Changed(); }
    }

    public PedalAxis SelectedAxis
    {
        get => selectedAxis;
        set
        {
            engine.RevokeRelease();
            selectedAxis = value;
            CancelCalibration();
            Changed();
            Changed(nameof(Editor));
            Changed(nameof(AxisTitle));
            UpdateCurve();
            RefreshDraftCurve();
            Refresh();
        }
    }

    public AxisEditor Editor => editors[selectedAxis];
    public string AxisTitle => SelectedAxis + " processing";
    public string ActiveProfileInfo => "Applied: " + applied.Name;
    public string SourceName { get; set; } = "No source selected";
    public string Status { get; private set; } = "Stopped";
    public bool IsReceiving { get; private set; }
    public string SampleInfo { get; private set; } = "Start live input or open a recorded capture";
    public string ObservedRange { get; private set; } = "—";
    public string CorrectionInfo { get; private set; } = "Applied 0 · target 0";
    public string ResetInfo { get; private set; } = "No session";
    public string Message { get; private set; } = "Device-axis units · hardware calibration is separate · edits take effect with Apply";
    public string HardwareInfo { get; set; } = "Not read. Use Read hardware calibration to inspect the stored bounds.";
    public IReadOnlyList<PlotSample> History { get; private set; } = [];
    public IReadOnlyList<PlotSample> Curve { get; private set; } = [];

    public void Refresh()
    {
        var frame = engine.Snapshot(selectedAxis);
        foreach (var card in Cards)
        {
            card.Update(frame.Axes.Single(axis => axis.Axis == card.Axis));
        }

        var selected = frame.Axes.Single(axis => axis.Axis == selectedAxis);
        IsReceiving = selected.IsFresh;
        Status = selected.IsFresh ? "Receiving" : frame.State == FeelVR.PedalConnectionState.Connected ? "Waiting for fresh input" : frame.Detail ?? frame.State.ToString();
        if (Status.StartsWith("Source error:", StringComparison.Ordinal))
        {
            Message = Status;
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
        Changed(string.Empty);
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
        ShowMessage("Observed bounds copied into the editor. Review them, then Apply.");
    }

    public void ResetEditor()
    {
        editors[selectedAxis] = AxisEditor.FromProfile(DashboardEngine.DefaultProfile().Axes[selectedAxis]);
        Changed(nameof(Editor));
        RefreshDraftCurve();
        ShowMessage("Device-range defaults are in the editor. Apply to activate them.");
    }

    public void ShowMessage(string message)
    {
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
        Changed(string.Empty);
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
        ShowMessage($"{axis} curve edited. Apply activates it; Save profile keeps it for later.");
    }

    public void SetCurvePreset(PedalAxis axis, string name)
    {
        if (!CurveKinds.Contains(name)) { throw new ArgumentException("Unknown curve preset.", nameof(name)); }
        editors[axis].CurveKind = name;
        RefreshDraftCurve();
        Changed(nameof(Editor));
        ShowMessage($"{axis}: {name} curve ready. Apply activates all pedal drafts.");
    }

    public void ClearCurve(PedalAxis axis)
    {
        editors[axis].CurveKind = "Linear";
        editors[axis].Exponent = "1";
        editors[axis].Points = "0:0; 1:1";
        RefreshDraftCurve();
        Changed(nameof(Editor));
        ShowMessage($"{axis} curve cleared to linear. Calibration, deadzones and filters retained. Apply to activate.");
    }

    public void CaptureReleased()
    {
        engine.SuspendOutput("calibration started");
        var (frame, axis) = FreshAxis();
        calibration = new(selectedAxis, frame.Connection, axis.Result!.Value.Processing.Input);
        CalibrationInfo = "Released captured. Press to your desired full-scale force/travel, hold, then capture pressed.";
        Changed(nameof(CalibrationInfo));
    }

    public void CapturePressed()
    {
        if (calibration is null || calibration.Axis != selectedAxis) { throw new InvalidOperationException("Capture released for this pedal first."); }
        var (frame, axis) = FreshAxis();
        var result = calibration.Complete(frame.Connection, axis.Result!.Value.Processing.Input);
        editors[selectedAxis] = Editor with
        {
            Minimum = result.Minimum.ToString("R", CultureInfo.InvariantCulture),
            Maximum = result.Maximum.ToString("R", CultureInfo.InvariantCulture),
            Inverted = result.Inverted
        };
        CancelCalibration();
        CalibrationInfo = "Calibration ready in the editor. Review endpoints and Apply; Save profile to persist.";
        Changed(nameof(Editor));
        Changed(nameof(CalibrationInfo));
    }

    public void CancelCalibration()
    {
        calibration = null;
        CalibrationInfo = "Release this pedal, then capture released. Software calibration only.";
        Changed(nameof(CalibrationInfo));
    }

    private (DashboardFrame Frame, AxisFrame Axis) FreshAxis()
    {
        var frame = engine.Snapshot(selectedAxis);
        var axis = frame.Axes.Single(a => a.Axis == selectedAxis);
        if (!axis.IsFresh || axis.Result is null) { throw new InvalidOperationException("Start live input and wait for fresh samples before calibration."); }
        return (frame, axis);
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
