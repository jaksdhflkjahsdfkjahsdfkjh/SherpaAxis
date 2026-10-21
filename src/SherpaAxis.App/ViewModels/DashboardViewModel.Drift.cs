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

    public void FixDrift()
    {
        SetCorrectionMode("By button");
        Editor.MaximumCorrection = (Editor.BuildProfile().Calibration.Span * .25).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        RefreshUnsavedState();
        engine.RequestFixDrift(SelectedAxis);
        RefreshDrift();
        ShowMessage("Keep your foot completely off the selected pedal. Fix drift requires one stable second and does not save hardware calibration.");
    }

    private void RefreshDrift()
    {
        var state = engine.DriftState(SelectedAxis);
        DriftStatus = state.Status;
        CanSaveCorrectedCalibration = state.CanSave;
        SensorRangeInfo = state.RawAvailable && state.Input is { } input
            ? FormattableString.Invariant($"Sensor input: {input:N0}\nOriginal raw range: {state.RawMinimum?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "—"} → {state.RawMaximum?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "—"}\nCalibrated hardware range: {state.Minimum - state.Offset:N0} → {state.Maximum - state.Offset:N0}\nCorrected hardware range: {state.Minimum:N0} → {state.Maximum:N0}\nCorrection: {state.Offset:+0;-0;0}") : "";
    }
}
