using SherpaAxis.App.Models;
using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

public sealed class PedalCurveViewModel(PedalAxis axis) : ObservableObject
{
    public PedalAxis Axis { get; } = axis;
    public string Title => Axis + " curve";
    public AxisEditor Editor { get; private set; } = new();
    public string CurveKind => Editor.CurveKind;
    public IReadOnlyList<CurvePoint> Points { get; private set; } = [];
    public IReadOnlyList<PlotSample> Preview { get; private set; } = [];
    public IReadOnlyList<PlotSample> Applied { get; private set; } = [];

    public void Refresh(AxisEditor editor, IReadOnlyList<PlotSample> applied)
    {
        if (!ReferenceEquals(Editor, editor))
        {
            Editor = editor;
            Changed(nameof(Editor));
        }
        try
        {
            var curve = Editor.BuildCurve();
            Points = CurvePresets.Handles(curve);
            var processor = new AxisProfile(new(0, 1), curve: curve).CreateProcessor();
            Preview = Enumerable.Range(0, 201).Select(i => new PlotSample(i / 200d, 0, 0, processor.Process(i / 200d).Output)).ToArray();
        }
        catch (ArgumentException) { Points = []; Preview = []; }
        Applied = applied;
        Changed(nameof(CurveKind));
        Changed(nameof(Points));
        Changed(nameof(Preview));
        Changed(nameof(Applied));
    }
}
