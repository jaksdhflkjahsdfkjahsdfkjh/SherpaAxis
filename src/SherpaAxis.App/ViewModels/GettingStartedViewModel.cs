using SherpaAxis.App.Models;

namespace SherpaAxis.App.ViewModels;

public sealed record TourStep(string Title, string Location, string Description, string Tip,
    string Target, SidePanelMode Panel = SidePanelMode.Tuning);

public sealed class GettingStartedViewModel : ObservableObject
{
    public static IReadOnlyList<TourStep> Steps { get; } =
    [
        new("Welcome to SherpaAxis", "Pedal input and game output",
            "SherpaAxis reads your pedals and sends the tuned output to a virtual controller for your game.",
            "Keep SherpaAxis open while playing. Close FEEL-VR Control, including its tray icon.", "MainSignalPlot"),
        new("Profiles", "Profile bar · top of the window",
            "Choose a profile from the list. Use + to create one, Save to keep changes and the dots menu for more actions.",
            "Changes apply immediately. Save the profile to keep them after restart. Auto-load opens this profile on the next launch.", "ProfileBar"),
        new("Shape each pedal", "Tuning · curves and filtering",
            "Drag curve points or choose a preset to change how pedal movement becomes game output.",
            "Each pedal has its own curve. Reset to linear restores a straight line.", "CurvesSection"),
        new("Filtering", "Tuning · Filtering",
            "Choose a filter preset and compare the result on the graph. Filter details lets you adjust it further.",
            "More smoothing reduces noise but adds delay. The information button shows recorded examples.", "FilteringPanel"),
        new("See what changes", "Live graph · above the curves",
            "Choose one pedal or all three, then select the signals to compare. The time field sets how many seconds are visible.",
            "Scroll to zoom, drag to select a range or double-click to reset. Pedal output keeps running.", "SignalGraphSection"),
        new("Correct pedal drift", "Tuning · Pedal drift correction",
            "Calibrate once in SherpaAxis first. Auto checks five matching press/release cycles before correcting drift. Mismatched cycles leave the range unchanged.",
            "For a manual fix, take your foot off and hold the drift button until the correction finishes. Save to controller keeps the fix in the pedals.", "DriftCorrectionPanel"),
        new("Calibrate the controller", "Calibration · side panel",
            "Calibration measures each pedal’s released and fully pressed positions. The tour does not start a capture.",
            "Close games and FEEL-VR Control first. Saving calibration writes to the pedals; saving a profile keeps curves and filters.", "CalibrationModeButton"),
        new("Game setup", "Settings · device setup and help",
            "In your game, bind the virtual pedals: X = throttle, Y = brake, Z = clutch. HidHide prevents duplicate input.",
            "Use Settings for device setup, firmware, language and startup options. The setup guide has more detail.", "HelpPanel", SidePanelMode.Settings)
    ];

    private int index;
    private bool isOpen;
    private bool interacted;
    public bool IsOpen { get => isOpen; set => Set(ref isOpen, value); }
    public bool Interacted { get => interacted; private set { if (Set(ref interacted, value)) { Changed(nameof(InteractionHint)); } } }
    public string InteractionHint => Current.Target is "CalibrationModeButton" or "DriftCorrectionPanel"
        ? "Choose Next to continue. No calibration or drift fix will start during the tour."
        : Interacted ? "Choose Next when ready." : "Try the highlighted controls or choose Next.";
    public TourStep Current => Steps[index];
    public int StepCount { get; } = Steps.Count;
    public int Progress => index + 1;
    public string StepLabel => $"Step {Progress} of {Steps.Count}";
    public bool CanGoBack => index > 0;
    public bool IsLast => index == Steps.Count - 1;
    public string NextLabel => IsLast ? "Finish" : "Next";

    public void Start() { MoveTo(0); IsOpen = true; }
    public void ObserveInteraction() => Interacted = true;

    public void MoveNext() { if (!IsLast) { MoveTo(index + 1); } }
    public void MoveBack() { if (CanGoBack) { MoveTo(index - 1); } }
    private void MoveTo(int next)
    {
        index = next;
        Interacted = false;
        Changed(nameof(Current)); Changed(nameof(Progress)); Changed(nameof(StepLabel));
        Changed(nameof(CanGoBack)); Changed(nameof(IsLast)); Changed(nameof(NextLabel)); Changed(nameof(InteractionHint));
    }
}
