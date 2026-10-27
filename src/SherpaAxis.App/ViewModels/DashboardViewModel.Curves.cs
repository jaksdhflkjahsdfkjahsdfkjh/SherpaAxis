using SherpaAxis.App.Models;
using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

public sealed partial class DashboardViewModel
{
    public string SelectedCurvePreset => Editor.CurveKind == "Power" ? "Custom" : Editor.CurveKind;
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
        Changed(nameof(SelectedCurvePreset));
    }

    public void EditCurve(IEnumerable<CurvePoint> points)
        => EditCurve(selectedAxis, points);

    public void EditCurve(PedalAxis axis, IEnumerable<CurvePoint> points)
    {
        editors[axis].SetCurvePoints(points);
        RefreshDraftCurve();
        Changed(nameof(Editor));
        NotifyFilterSettings();
        RefreshUnsavedState();
    }

    public void SetCurvePreset(PedalAxis axis, string name)
    {
        if (!CurveKinds.Contains(name)) { throw new ArgumentException("Unknown curve preset.", nameof(name)); }
        editors[axis].CurveKind = name;
        RefreshDraftCurve();
        Changed(nameof(Editor));
        NotifyFilterSettings();
        RefreshUnsavedState();
    }

    public void ClearCurve(PedalAxis axis)
    {
        editors[axis].CurveKind = "Linear";
        editors[axis].Exponent = "1";
        editors[axis].Points = "0:0; 1:1";
        RefreshDraftCurve();
        Changed(nameof(Editor));
        NotifyFilterSettings();
        RefreshUnsavedState();
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

    public static IReadOnlyList<string> CurveKinds { get; } = [.. CurvePresets.Names, "Custom"];

    public IReadOnlyList<PedalCurveViewModel> PedalCurves { get; } = PedalDisplayOrder.Select(axis => new PedalCurveViewModel(axis)).ToArray();

    public IReadOnlyList<CurvePoint> DraftPoints { get; private set; } = [];

    public IReadOnlyList<PlotSample> DraftCurve { get; private set; } = [];
}
