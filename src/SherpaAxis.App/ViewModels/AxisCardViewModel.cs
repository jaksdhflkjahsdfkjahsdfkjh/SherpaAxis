using System.Globalization;
using SherpaAxis.App.Models;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

public sealed class AxisCardViewModel(PedalAxis axis) : ObservableObject
{
    public PedalAxis Axis { get; } = axis;
    public string Name => Axis.ToString().ToUpperInvariant();
    private string input = "—";
    private string outputText = "—";
    private string filtered = "No samples";
    private double outputPercent;
    private bool isFresh;
    private string inputLabel = "Device input: ";
    public string Input { get => input; private set => Set(ref input, value); }
    public string OutputText { get => outputText; private set => Set(ref outputText, value); }
    public string Filtered { get => filtered; private set => Set(ref filtered, value); }
    public double OutputPercent { get => outputPercent; private set => Set(ref outputPercent, value); }
    public bool IsFresh { get => isFresh; private set => Set(ref isFresh, value); }
    public string InputLabel { get => inputLabel; private set => Set(ref inputLabel, value); }

    public void UpdateControllerInput(int? counts, bool sensorCounts)
    {
        IsFresh = counts.HasValue;
        Input = counts?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        InputLabel = sensorCounts ? "Sensor input: " : "Device input: ";
        OutputPercent = counts.HasValue && !sensorCounts ? counts.Value / DashboardEngine.DeviceMaximum(Axis) * 100 : 0;
        OutputText = !counts.HasValue ? "—" : sensorCounts ? "Sensor" : OutputPercent.ToString("F1", CultureInfo.InvariantCulture) + "%";
        Filtered = "Controller input · no software processing";
    }

    public void Update(AxisFrame frame, bool sensorCounts = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        IsFresh = frame.IsFresh;
        InputLabel = sensorCounts ? "Sensor input: " : "Device input: ";
        Input = frame.Result?.Processing.Input.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        OutputPercent = (frame.FinalOutput ?? frame.Result?.Processing.Output ?? 0) * 100;
        OutputText = frame.Result is null ? "—" : OutputPercent.ToString("F1", CultureInfo.InvariantCulture) + "%";
        Filtered = frame.Result is null ? "No samples" : FormattableString.Invariant($"Filtered  {frame.Result.Value.Processing.FilteredNormalized:P1}");
    }
}
