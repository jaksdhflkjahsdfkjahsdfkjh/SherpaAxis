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
    public string BitePointStatus => !HasBitePointBinding ? "Bind a button to use the clutch bite point."
        : bitePointState.Holding ? FormattableString.Invariant($"Holding clutch at {bitePointState.Output:P0} minimum · press the pedal farther to override")
        : bitePointState.Enabled ? "Enabled · hold the button to apply the bite point"
        : "Disabled · double-tap the button to enable";
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
        RefreshBitePoint();
    }
}
