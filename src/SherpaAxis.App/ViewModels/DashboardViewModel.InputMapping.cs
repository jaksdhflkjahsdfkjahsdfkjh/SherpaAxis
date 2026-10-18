using System.Collections.ObjectModel;
using System.Globalization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

public sealed partial class DashboardViewModel
{
    private bool isCalibrating;
    public bool IsCalibrating => isCalibrating;
    public string CalibrationButtonText => IsCalibrating ? "Done" : "Calibrate software";

    public string CalibrationInfo { get; private set; } = "Start input, then Calibrate software. Done maps the captured controller output to 0–100% for this profile.";
    public string CalibrationRangeInfo { get; private set; } = "";

    public void CaptureEndpoint(bool maximum)
    {
        var frame = engine.Snapshot(selectedAxis).Axes.Single(axis => axis.Axis == selectedAxis);
        if (!frame.IsFresh || frame.Result is null)
        {
            throw new InvalidOperationException("A fresh sample is required. Start live input or playback first.");
        }

        var value = frame.Result.Value.Processing.Input.ToString("R", CultureInfo.InvariantCulture);
        editors[selectedAxis] = maximum ? Editor with { Maximum = value } : Editor with { Minimum = value };
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage("Captured into the editor. Review the bounds, then Apply.");
    }

    public void UseObservedRange()
    {
        var frame = engine.Snapshot(selectedAxis).Axes.Single(axis => axis.Axis == selectedAxis);
        if (!frame.ObservedMinimum.HasValue || frame.ObservedMaximum <= frame.ObservedMinimum)
        {
            throw new InvalidOperationException("Move the selected pedal through its range first; minimum and maximum must differ.");
        }

        editors[selectedAxis] = Editor with
        {
            Minimum = frame.ObservedMinimum.Value.ToString("R", CultureInfo.InvariantCulture),
            Maximum = frame.ObservedMaximum!.Value.ToString("R", CultureInfo.InvariantCulture)
        };
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage("Observed bounds copied into the editor. Review them, then Apply.");
    }

    public void ResetEditor()
    {
        CancelCalibration();
        editors[selectedAxis] = AxisEditor.FromProfile(DashboardEngine.DefaultProfile().Axes[selectedAxis]);
        Changed(nameof(Editor));
        NotifyFilterSettings();
        RefreshDraftCurve();
        ShowMessage("Device-range defaults are in the editor. Apply to activate them.");
    }

    public void ResetInputMapping()
    {
        CancelCalibration();
        editors[selectedAxis] = Editor with
        {
            Minimum = "0",
            Maximum = DashboardEngine.DeviceMaximum(selectedAxis).ToString("R", CultureInfo.InvariantCulture),
            Inverted = false
        };
        Changed(nameof(Editor));
        ShowMessage("Full controller range is in the draft. Apply settings to activate. Curves, filters and deadzones are retained.");
    }

    public void CancelCalibration()
    {
        engine.CancelCalibration();
        SetCalibrating(false);
        CalibrationInfo = "Start input, then Calibrate software. Done maps the captured controller output to 0–100% for this profile.";
        CalibrationRangeInfo = "";
        Changed(nameof(CalibrationInfo));
        Changed(nameof(CalibrationRangeInfo));
    }

    public void AdvanceCalibration()
    {
        try
        {
            if (!IsCalibrating)
            {
                engine.StartCalibration(selectedAxis);
                SetCalibrating(true);
                CalibrationInfo = $"Release and press {SelectedAxis} through its full desired range, then click Done. All input reports are captured.";
                RefreshCalibration();
            }
            else
            {
                applied = engine.CompleteCalibration(selectedAxis);
                var result = applied.Axes[selectedAxis].Calibration;
                editors[selectedAxis] = Editor with
                {
                    Minimum = result.Minimum.ToString("R", CultureInfo.InvariantCulture),
                    Maximum = result.Maximum.ToString("R", CultureInfo.InvariantCulture),
                    Inverted = false
                };
                SetCalibrating(false);
                CalibrationInfo = "Software mapping applied. Minimum = 0%, maximum = 100%. Save profile to keep it after restart. Other draft settings still require Apply.";
                CalibrationRangeInfo = FormattableString.Invariant($"0% = {result.Minimum:N0}\n100% = {result.Maximum:N0}");
                Changed(nameof(CalibrationRangeInfo));
                Changed(nameof(Editor));
                NotifyFilterSettings();
                UpdateCurve();
                RefreshDraftCurve();
                Refresh();
                ShowMessage($"{SelectedAxis} calibration applied immediately. Virtual output stays disarmed; Save profile to keep these bounds.");
            }
            Changed(nameof(CalibrationInfo));
        }
        catch (InvalidOperationException exception)
        {
            CalibrationInfo = exception.Message;
            Changed(nameof(CalibrationInfo));
            RefreshCalibration();
            throw;
        }
    }

    private void SetCalibrating(bool value)
    {
        isCalibrating = value;
        Changed(nameof(IsCalibrating));
        Changed(nameof(CalibrationButtonText));
    }

    private void RefreshCalibration()
    {
        if (!IsCalibrating) { return; }
        var capture = engine.CalibrationSnapshot();
        if (capture is not { IsActive: true } || capture.Axis != selectedAxis)
        {
            SetCalibrating(false);
            CalibrationInfo = capture?.Failure ?? "Software calibration cancelled. Click Calibrate software to start again.";
            CalibrationRangeInfo = "";
            Changed(nameof(CalibrationInfo));
            Changed(nameof(CalibrationRangeInfo));
            ShowMessage(CalibrationInfo);
            return;
        }
        var range = capture.SampleCount == 0 ? "Waiting for the first input report…"
            : FormattableString.Invariant($"0% = {capture.Minimum:N0}\n100% = {capture.Maximum:N0}\nInput: {capture.Current:N0}");
        if (CalibrationRangeInfo != range)
        {
            CalibrationRangeInfo = range;
            Changed(nameof(CalibrationRangeInfo));
        }
    }
}
