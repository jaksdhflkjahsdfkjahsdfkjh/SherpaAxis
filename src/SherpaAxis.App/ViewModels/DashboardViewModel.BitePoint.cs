using SherpaAxis.App.Models;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

public sealed partial class DashboardViewModel
{
    private ClutchBitePointState bitePointState = new(false, false, .5);
    public bool IsClutchSelected => SelectedAxis == PedalAxis.Clutch;
    public bool BitePointEnabled => bitePointState.Enabled;
    public bool HasBitePointBinding => editors[PedalAxis.Clutch].BitePointButton is not null;
    public string BitePointBindingLabel => ButtonLabel(editors[PedalAxis.Clutch].BitePointButton);
    public static IReadOnlyList<string> BitePointModes { get; } = ["Hold bite point", "Hold full range"];
    public string SelectedBitePointMode => editors[PedalAxis.Clutch].BitePointMode == ClutchBitePointMode.HoldFullRange ? "Hold full range" : "Hold bite point";
    public string BitePointHint => editors[PedalAxis.Clutch].BitePointMode == ClutchBitePointMode.HoldFullRange
        ? "Double-tap to turn on or off. Hold for 250 ms to use full range; release to use the percentage below. At 40%, a fully pressed pedal gives 100% while held and 40% after release."
        : "Double-tap to turn on or off. Hold for 250 ms to keep the clutch at the percentage below. Release to return to the pedal. Press farther for more clutch.";
    public string BitePointStatus => !HasBitePointBinding ? "Bind a button to use the clutch bite point."
        : !bitePointState.Enabled ? "Disabled · double-tap the button to enable"
        : bitePointState.Mode == ClutchBitePointMode.HoldFullRange
            ? bitePointState.Holding ? FormattableString.Invariant($"Full clutch range · release the button to use {bitePointState.Output:P0} range")
                : FormattableString.Invariant($"Clutch range limited to {bitePointState.Output:P0} · hold the button for full range")
        : bitePointState.Holding ? FormattableString.Invariant($"Holding clutch at {bitePointState.Output:P0} minimum · press the pedal farther to override")
        : "Enabled · hold the button to apply the bite point";
    public void SetBitePointMode(string mode)
    {
        editors[PedalAxis.Clutch].BitePointMode = mode switch
        {
            "Hold bite point" => ClutchBitePointMode.HoldMinimum,
            "Hold full range" => ClutchBitePointMode.HoldFullRange,
            _ => throw new ArgumentException("Unknown clutch bite point mode.", nameof(mode))
        };
        RefreshUnsavedState(); NotifyBitePointSettings();
    }
    private static string ButtonLabel(CorrectionButton? button) => button is null ? "No button bound" : button.Kind == "keyboard"
        ? "Key: " + System.Windows.Input.KeyInterop.KeyFromVirtualKey(button.Code) : $"Controller button {button.Code}";
    public void BindBitePoint(CorrectionButton button)
    {
        editors[PedalAxis.Clutch].BitePointButton = button;
        RefreshUnsavedState(); NotifyBitePointSettings();
    }
    public void ClearBitePointBinding()
    {
        editors[PedalAxis.Clutch].BitePointButton = null;
        RefreshUnsavedState(); NotifyBitePointSettings();
    }
    public void SetBitePointEnabled(bool enabled) { engine.SetBitePointEnabled(enabled); RefreshBitePoint(); }
    private void RefreshBitePoint()
    {
        var next = engine.BitePointState;
        if (bitePointState == next) { return; }
        bitePointState = next;
        Changed(nameof(BitePointEnabled)); Changed(nameof(BitePointStatus));
        Changed(nameof(GraphLegend)); NotifyDisplay();
    }
    private void NotifyBitePointSettings()
    {
        Changed(nameof(IsClutchSelected)); Changed(nameof(HasBitePointBinding));
        Changed(nameof(BitePointBindingLabel)); Changed(nameof(BitePointStatus));
        Changed(nameof(SelectedBitePointMode)); Changed(nameof(BitePointHint));
        RefreshBitePoint();
    }
}
