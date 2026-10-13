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
    public string InputLabel { get; private set; } = "Device input: ";

    public void UpdateControllerInput(int? counts, bool sensorCounts)
    {
        IsFresh = counts.HasValue;
        Input = counts?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        InputLabel = sensorCounts ? "Sensor input: " : "Device input: ";
        OutputPercent = counts.HasValue && !sensorCounts ? counts.Value / DashboardEngine.DeviceMaximum(Axis) * 100 : 0;
        OutputText = !counts.HasValue ? "—" : sensorCounts ? "Sensor" : OutputPercent.ToString("F1", CultureInfo.InvariantCulture) + "%";
        Filtered = "Controller input · no software processing";
        Notify();
    }

    public void Update(AxisFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        IsFresh = frame.IsFresh;
        InputLabel = "Device input: ";
        Input = frame.Result?.Processing.Input.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        OutputPercent = (frame.Result?.Processing.Output ?? 0) * 100;
        OutputText = frame.Result is null ? "—" : OutputPercent.ToString("F1", CultureInfo.InvariantCulture) + "%";
        Filtered = frame.Result is null ? "No samples" : FormattableString.Invariant($"Filtered  {frame.Result.Value.Processing.FilteredNormalized:P1}");
        Notify();
    }

    private void Notify()
    {
        Changed(nameof(InputLabel));
        Changed(nameof(IsFresh));
        Changed(nameof(Input));
        Changed(nameof(OutputPercent));
        Changed(nameof(OutputText));
        Changed(nameof(Filtered));
    }
}
