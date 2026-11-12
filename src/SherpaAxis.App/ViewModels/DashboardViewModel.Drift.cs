using SherpaAxis.App.Models;

namespace SherpaAxis.App.ViewModels;

public sealed partial class DashboardViewModel
{
    private string driftStatus = "Select a drift correction mode to start monitoring";
    public string DriftStatus { get => driftStatus; private set => Set(ref driftStatus, value); }
    private string sensorRangeInfo = "";
    public string SensorRangeInfo { get => sensorRangeInfo; private set => Set(ref sensorRangeInfo, value); }
    private bool canSaveCorrectedCalibration;
    public bool CanSaveCorrectedCalibration { get => canSaveCorrectedCalibration; private set => Set(ref canSaveCorrectedCalibration, value); }
    private bool driftInputReady;
    public bool CanFixDrift => CanTune && driftInputReady;
    private string driftSaveHint = "";
    public string DriftSaveHint { get => CanTune ? driftSaveHint : "Wait for the current operation to finish."; private set => Set(ref driftSaveHint, value); }
    private string driftFixHint = "";
    public string DriftFixHint { get => CanTune ? driftFixHint : "Wait for the current operation to finish."; private set => Set(ref driftFixHint, value); }
    private double driftCheckProgress;
    public double DriftCheckProgress { get => driftCheckProgress; private set => Set(ref driftCheckProgress, value); }
    private bool showDriftProgress;
    public bool ShowDriftProgress { get => showDriftProgress; private set => Set(ref showDriftProgress, value); }
    private string fixDriftLabel = "Hold to fix drift";
    public string FixDriftLabel { get => fixDriftLabel; private set => Set(ref fixDriftLabel, value); }

    private void NotifyDriftAvailability()
    { Changed(nameof(CanFixDrift)); Changed(nameof(DriftFixHint)); Changed(nameof(DriftSaveHint)); }

    public void FixDrift()
    {
        SetCorrectionMode("By button");
        Editor.MaximumCorrection = (Editor.BuildProfile().Calibration.Span * .25).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        RefreshUnsavedState();
        engine.RequestFixDrift(SelectedAxis);
        RefreshDrift();
        ShowMessage("Keep your foot off and hold until the fix finishes. Save to controller to keep the correction.");
    }

    private void RefreshDrift()
    {
        var state = engine.DriftState(SelectedAxis);
        DriftStatus = state.CheckStage switch
        {
            DriftCheckStage.Checking => FormattableString.Invariant($"Keep the pedal released · {state.StableSeconds:0.0}/1.0 s stable"),
            DriftCheckStage.Applying => "Keep holding while the correction is applied.",
            _ => state.Status
        };
        CanSaveCorrectedCalibration = state.CanSave;
        Set(ref driftInputReady, state.CanFix, nameof(CanFixDrift));
        DriftFixHint = state.CanFix ? "Take your foot off the pedal and keep holding until the correction finishes." : state.Status;
        DriftSaveHint = state.CanSave ? "" : !state.RawAvailable ? "Sensor data is required before saving a correction." : state.SaveDisabledReason;
        ShowDriftProgress = state.CheckStage != DriftCheckStage.None;
        DriftCheckProgress = state.CheckStage switch
        {
            DriftCheckStage.Checking => Math.Clamp(state.StableSeconds, 0, 1),
            DriftCheckStage.Applying or DriftCheckStage.Complete => 1,
            _ => 0
        };
        FixDriftLabel = state.CheckStage switch
        {
            DriftCheckStage.Checking => "Checking release…",
            DriftCheckStage.Applying => "Applying correction…",
            DriftCheckStage.Complete => Math.Abs(state.Offset) < .5 ? "No drift found" : "Drift corrected",
            _ => "Hold to fix drift"
        };
        SensorRangeInfo = state.RawAvailable && state.Input is { } input
            ? FormattableString.Invariant($"Sensor input: {input:N0}\nOriginal raw range: {state.RawMinimum?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "—"} → {state.RawMaximum?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "—"}\nCalibrated hardware range: {state.Minimum - state.Offset:N0} → {state.Maximum - state.Offset:N0}\nCorrected hardware range: {state.Minimum:N0} → {state.Maximum:N0}\nCorrection: {state.Offset:+0;-0;0}") : "";
    }
}
