using System.Globalization;
using SherpaAxis.App.Models;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

public sealed class AxisCardViewModel(PedalAxis axis) : ObservableObject
{
    public PedalAxis Axis { get; } = axis;
    public string Name => Axis.ToString().ToUpperInvariant();
    public string Input { get; private set; } = "—";
    public string OutputText { get; private set; } = "—";
    public string Filtered { get; private set; } = "No samples";
    public double OutputPercent { get; private set; }
    public bool IsFresh { get; private set; }

    public void Update(AxisFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        IsFresh = frame.IsFresh;
        Input = frame.Result?.Processing.Input.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        OutputPercent = (frame.Result?.Processing.Output ?? 0) * 100;
        OutputText = frame.Result is null ? "—" : OutputPercent.ToString("F1", CultureInfo.InvariantCulture) + "%";
        Filtered = frame.Result is null ? "No samples" : FormattableString.Invariant($"Filtered  {frame.Result.Value.Processing.FilteredNormalized:P1}");
        Changed(nameof(IsFresh));
        Changed(nameof(Input));
        Changed(nameof(OutputPercent));
        Changed(nameof(OutputText));
        Changed(nameof(Filtered));
    }
}
