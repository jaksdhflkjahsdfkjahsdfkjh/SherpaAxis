using SherpaAxis.App.Models;

namespace SherpaAxis.App.ViewModels;

public sealed partial class DashboardViewModel
{
    public IReadOnlyList<PedalHistory> PedalHistories { get; private set; } = [];

    public static IReadOnlyList<string> GraphPedals { get; } = ["All pedals", "Throttle", "Brake", "Clutch"];
    public static IReadOnlyList<string> GraphModes { get; } = ["Input vs filtered", "Filtered vs curved", "Output", "Input", "Filtered", "Filter difference", "Device counts"];
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
    public string GraphLegend => (GraphMode switch
    {
        "Input vs filtered" => "Dashed: input · Color: filtered · Before curve/deadzones · Applied settings",
        "Filtered vs curved" => "Dashed: filtered input · Color: output after deadzones and curve · Applied settings",
        "Filter difference" => "Input minus filtered · Auto-scaled in percentage points (pp) · Before curve/deadzones",
        "Device counts" => "Original device counts · Each pedal has its own scale · Applied settings",
        "Output" => "Final output after filter, deadzones and curve · Applied settings",
        _ => "Normalized signal before deadzones and curve · Applied settings"
    }) + (bitePointState.Holding ? " · Clutch bite point hold active" : "");
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
}
