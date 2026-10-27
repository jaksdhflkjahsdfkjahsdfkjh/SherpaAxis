using SherpaAxis.App.Models;

namespace SherpaAxis.App.ViewModels;

public sealed record TourStep(string Title, string Location, string Description, string Tip,
    string Target, SidePanelMode Panel = SidePanelMode.Tuning);

public sealed class GettingStartedViewModel : ObservableObject
{
    public static IReadOnlyList<TourStep> Steps { get; } =
    [
        new("Welcome to SherpaAxis", "Your pedals, tuned your way",
            "SherpaAxis reads your FEEL-VR pedals, applies your tuning and sends the result to a virtual controller. Live input and output start automatically when the device is ready.",
            "Keep SherpaAxis running while driving. Close FEEL-VR Control completely, including its tray instance, to avoid conflicting pedal settings.", "MainSignalPlot"),
        new("A profile for every setup", "Profile bar · top of the window",
            "Choose a profile from the list. The + button creates and selects a new profile. Save keeps your changes; the dots menu includes rename, duplicate, import, export and delete.",
            "Tuning changes apply automatically, but you must save the profile to keep them after restart. Auto-load selects one profile for the next launch.", "ProfileBar"),
        new("Shape each pedal", "Tuning · curves and filtering",
            "Select a pedal to adjust its response. Drag points on its curve, choose a curve preset or use Clear for a linear response. Profile deadzones trim the released and fully pressed ends.",
            "Try dragging a curve point or choosing a preset. Each pedal keeps its own curve. Your changes apply to the current profile.", "CurvesSection"),
        new("Find the right filtering", "Tuning · Filtering",
            "Try Off, Sharp, Balanced or Smooth and compare the result on the graph. Open Filter details to choose an algorithm or adjust its timing.",
            "Start with Balanced filtering, then compare the result. The information button beside Filtering explains every algorithm with recorded examples. More smoothing can add delay.", "FilteringPanel"),
        new("See what changes", "Live graph · above the curves",
            "Show one pedal or all three. Compare Input vs filtered to see smoothing, Filtered vs curved to see the response curve, or Filter difference for small changes. Enter the visible time range in seconds.",
            "Pause to inspect the graph, use the mouse wheel to zoom or drag across a time range to enlarge it. Reset zoom restores the view. These controls do not pause pedal output.", "SignalGraphSection"),
        new("Correct pedal drift", "Tuning · Pedal drift correction",
            "Calibrate the pedal once in SherpaAxis to retain its original raw range. Auto requires five consistent press/release cycles with stable releases. Mismatched cycles do not apply a new correction.",
            "Hold Fix drift or your bound control with your foot completely off for at least one stable second. Keep holding until correction finishes; release to stop. Correction is temporary until saved. Stability alone cannot rule out steady foot pressure.", "DriftCorrectionPanel"),
        new("Calibrate the controller", "Calibration · side panel",
            "This tab opens hardware calibration. The tour only highlights the button; it does not connect a calibration session or capture values. Finish the tour before calibrating.",
            "Calibrate with games and FEEL-VR Control closed. Device tools can back up and restore hardware calibration. Hardware calibration is separate from profile curves, filters and deadzones.", "CalibrationModeButton"),
        new("Ready for your next drive", "Settings · device setup and help",
            "Bind the virtual device's X axis to throttle, Y to brake and Z to clutch in your game. HidHide prevents duplicate physical input while SherpaAxis runs; keep the physical HID driver installed.",
            "Settings includes device checks, automatic vJoy setup, firmware updates, language and startup options. Open Setup guide for details, or Getting started to replay this tour. Firmware is flashed only when you explicitly choose Flash.", "HelpPanel", SidePanelMode.Settings)
    ];

    private int index;
    private bool isOpen;
    private bool interacted;
    public bool IsOpen { get => isOpen; set => Set(ref isOpen, value); }
    public bool Interacted { get => interacted; private set { if (Set(ref interacted, value)) { Changed(nameof(InteractionHint)); } } }
    public string InteractionHint => Current.Target is "CalibrationModeButton" or "DriftCorrectionPanel"
        ? "Review these controls, then choose Next. The tour does not start hardware actions."
        : Interacted ? "You tried this area. Continue when ready." : "Try the highlighted controls, or choose Next to continue.";
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
