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

    public DashboardViewModel(DashboardEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.engine = engine;
        Load(applied);
        ShowMessage("Start live input or open a capture. All editable settings affect the software preview; hardware calibration is separate.");
    }

    public IReadOnlyList<AxisCardViewModel> Cards { get; } = Enum.GetValues<PedalAxis>().Select(axis => new AxisCardViewModel(axis)).ToArray();
    public static IReadOnlyList<PedalAxis> AxisNames { get; } = Enum.GetValues<PedalAxis>();
    public static IReadOnlyList<string> CurveKinds { get; } = ["Linear", "Power", "Custom"];
    public static IReadOnlyList<double> PlaybackSpeeds { get; } = [1, 5, 20];
    public double PlaybackSpeed { get; set; } = 1;
    public bool CanEdit { get; set; } = true;
    public bool PlotCounts
    {
        get => plotCounts;
        set { plotCounts = value; Changed(); }
    }

    public string ProfileName
    {
        get => profileName;
        set { profileName = value; Changed(); }
    }

    public PedalAxis SelectedAxis
    {
        get => selectedAxis;
        set
        {
            engine.RevokeRelease();
            selectedAxis = value;
            Changed();
            Changed(nameof(Editor));
            Changed(nameof(AxisTitle));
            UpdateCurve();
            Refresh();
        }
    }

    public AxisEditor Editor => editors[selectedAxis];
    public string AxisTitle => SelectedAxis + " processing";
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
        ShowMessage("Applied to software preview. All correction and filter histories were cleared.");
    }

    public void Load(PedalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var defaults = DashboardEngine.DefaultProfile();
        foreach (var axis in AxisNames)
        {
            editors[axis] = AxisEditor.FromProfile(profile.Axes.TryGetValue(axis, out var value) ? value : defaults.Axes[axis]);
        }

        ProfileName = profile.Name;
        Apply();
        Changed(nameof(Editor));
        ShowMessage("Profile loaded. Missing axes use device-range defaults; session transition limits use app defaults. Check capture units before testing.");
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
        ShowMessage("Device-range defaults are in the editor. Apply to activate them.");
    }

    public void ShowMessage(string message)
    {
        Message = message;
        Changed(nameof(Message));
    }

    private void UpdateCurve()
    {
        var profile = applied.Axes[selectedAxis];
        var processor = new AxisProfile(profile.Calibration, profile.Deadzone, profile.Curve).CreateProcessor();
        Curve = Enumerable.Range(0, 201).Select(index =>
        {
            var fraction = index / 200d;
            var input = profile.Calibration.Inverted ? profile.Calibration.Maximum - fraction * profile.Calibration.Span
                : profile.Calibration.Minimum + fraction * profile.Calibration.Span;
            input = Math.Clamp(input, profile.Calibration.Minimum, profile.Calibration.Maximum);
            return new PlotSample(fraction, fraction, fraction, processor.Process(input).Output);
        }).ToArray();
        Changed(nameof(Curve));
    }
}
