using SherpaAxis.App.Services.Hardware;
using SherpaAxis.Core;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed partial class DashboardEngine
{
    private SensorCalibration? sensorCalibration;
    private PedalAxis? fixRequested;
    private long fixRequestedAt;
    private string? driftInputError;
    private HidDeviceIdentity? ownedControllerIdentity;
    public HidDeviceIdentity? OwnedControllerIdentity { get { lock (sync) { return ownedControllerIdentity; } } }
    internal PedalCalibration? CalibrationBounds { get { lock (sync) { return sensorCalibration?.Bounds ?? hardware?.Calibration; } } }
    internal void SetOwnedControllerIdentity(HidDeviceIdentity? identity) { lock (sync) { ownedControllerIdentity = identity; } }
    public bool RequiresSensorInput { get { lock (sync) { return activeProfile.Axes.Values.Any(axis => axis.CorrectionMode != BaselineCorrectionMode.Off); } } }

    public void SetDriftInputError(string message) { lock (sync) { driftInputError = message; } }
    public void SetSensorCalibration(SensorCalibration? calibration)
    {
        if (calibration is not null)
        {
            PedalHardwareSettings.Validate(calibration.Bounds);
            if (calibration.RawRanges is not null)
            {
                foreach (var (axis, raw) in calibration.RawRanges)
                { SensorReferenceStore.Validate(new(calibration.SerialNumber, (PedalChannel)axis, raw, calibration.Range(axis))); }
            }
        }
        lock (sync)
        {
            if (sensorCalibration == calibration) { return; }
            sensorCalibration = calibration;
            driftInputError = null;
            ClearConnection();
            Configure(activeProfile, activeLimits);
        }
    }

    private AxisProfile ProfileForInput(PedalAxis axis, AxisProfile profile) => sensorCalibration?.Map(axis, profile) ?? profile;

    public void RequestFixDrift(PedalAxis axis)
    {
        lock (sync)
        {
            if (activeProfile.Axes[axis].CorrectionMode != BaselineCorrectionMode.ByButton)
            { throw new InvalidOperationException("Select By button mode before fixing drift."); }
            if (sensorCalibration is not null && sensorCalibration.RawRange(axis) is null)
            { throw new InvalidOperationException("Original raw calibration is unavailable. Calibrate this pedal in SherpaAxis first."); }
            fixRequested = axis;
            fixRequestedAt = clock.GetTimestamp();
            axes[axis].DriftStatus = "Hold for 1 second to set the current sensor reading as the release point.";
        }
    }

    public void CancelFixDrift(PedalAxis axis)
    {
        lock (sync)
        {
            var requested = fixRequested == axis;
            if (requested) { fixRequested = null; }
            var values = axes[axis];
            if (!requested && !values.ManualCapture && !values.ManualCaptured) { return; }
            values.ManualCapture = values.ManualCaptured = false;
            values.ManualStarted = null;
            values.DriftStatus = values.BaselineApproved && values.Session.BaselineCorrection == values.Session.TargetCorrection
                ? Math.Abs(values.Session.BaselineCorrection) < .5 ? "Released baseline matches calibration; no correction needed"
                    : "Drift fixed for this session. Save corrected calibration to keep it in the controller"
                : "Drift check cancelled; no new correction";
        }
    }

    private bool ProcessDrift(PedalAxis axis, AxisState values, int raw, AxisProfile settings)
    {
        if (sensorCalibration is null || values.CarryCorrection is not null) { return false; }
        if (sensorCalibration.RawRange(axis) is null)
        {
            if (fixRequested == axis) { fixRequested = null; }
            return false;
        }
        var physical = sensorCalibration.PhysicalAxis(axis);
        var gap = activeLimits[axis].ResetAfterSeconds;
        var maximum = values.Session.MaximumBaselineCorrection;
        if (settings.CorrectionMode == BaselineCorrectionMode.Auto)
        {
            var baseline = values.Automatic.Observe(raw, elapsed, connection, physical, maximum, gap);
            if (baseline is not { } candidate) { return false; }
            var offset = candidate - physical.Minimum;
            if (values.Session.BaselineCorrection == values.Session.TargetCorrection
                && Math.Abs(offset - values.Session.BaselineCorrection) <= values.Automatic.NoiseTolerance)
            {
                values.Automatic.Reject(FormattableString.Invariant($"Release difference is within measured sensor noise (±{values.Automatic.NoiseTolerance:0} counts); no new correction needed"));
                return false;
            }
            if (offset > values.Session.BaselineCorrection)
            {
                values.Automatic.Reject(FormattableString.Invariant($"Five cycles agree, but an upward shift of {offset - values.Session.BaselineCorrection:0} counts could be foot pressure. Use Fix drift with your foot off"));
                return false;
            }
            if (!ValidShift(axis, offset))
            { values.Automatic.Reject("Estimated drift exceeds the sensor range; full calibration is required"); return false; }
            if (!CanAdjustAtRelease(values, raw, offset, settings))
            { values.Automatic.Suspend("Cycles agree; waiting for released output before applying correction"); return false; }
            values.Session.ConfirmReleasedBaseline(ProfileForInput(axis, settings).Calibration.ReleasedReference + offset);
            values.BaselineApproved = true;
            return true;
        }
        if (fixRequested == axis)
        {
            if (clock.GetElapsedTime(fixRequestedAt) >= TimeSpan.FromSeconds(10))
            {
                fixRequested = null;
                values.DriftStatus = "Drift request expired. Release the button and try again.";
                return false;
            }
            values.ManualCapture = true; values.ManualCaptured = false; values.ManualStarted = elapsed;
            fixRequested = null;
        }
        var held = values.BindingReady && values.BindingWasDown && values.AuthorizedAt is { } authorized
            && clock.GetElapsedTime(authorized) < TimeSpan.FromMilliseconds(150);
        if (!values.ManualCapture && !values.ManualCaptured && !held)
        {
            if (values.BindingWasDown)
            {
                values.BindingWasDown = values.BindingReady = false; values.AuthorizedAt = null;
                values.ManualStarted = null; values.ButtonCaptured = false;
                values.DriftStatus = "Drift check stopped; release the bound button before trying again";
            }
            return false;
        }
        if (values.ManualCaptured || values.ButtonCaptured) { return false; }
        values.ManualStarted ??= elapsed;
        var heldSeconds = elapsed - values.ManualStarted.Value;
        values.DriftStatus = FormattableString.Invariant($"Hold to set the release point: {Math.Clamp(heldSeconds, 0, 1):F1}/1.0 s");
        if (heldSeconds + 1e-9 < 1) { return false; }
        var correction = raw - physical.Minimum;
        if (!ValidShift(axis, correction))
        {
            values.ManualCapture = false; values.ManualStarted = null;
            values.BindingReady = false; values.ButtonCaptured = false;
            values.DriftStatus = "The shifted calibration would exceed the sensor range. Recalibrate this pedal.";
            return false;
        }
        values.Session.ApplyManualBaselineCorrection(correction);
        values.ManualCaptured = values.ManualCapture;
        values.ManualCapture = false;
        values.ButtonCaptured = held;
        values.BaselineApproved = true;
        values.DriftStatus = Math.Abs(correction) < .5 ? "Released baseline matches calibration; no correction needed"
            : "Drift fixed for this session. Save corrected calibration to keep it in the controller";
        return false;
    }

    private bool ValidShift(PedalAxis axis, double offset)
    {
        var range = sensorCalibration!.Range(axis);
        var raw = sensorCalibration.RawRange(axis);
        return raw is not null && range.Minimum + offset >= 0 && range.Maximum + offset <= DeviceMaximum(axis)
            && raw.Minimum + offset >= 0 && raw.Maximum + offset <= DeviceMaximum(axis);
    }

    private static bool CanAdjustAtRelease(AxisState values, double raw, double offset, AxisProfile settings)
    {
        var expected = settings.Calibration.Inverted ? 1d : 0d;
        return values.Current is { } current && Math.Abs(current.Processing.Output - expected) <= .001
            && Math.Abs(values.ReleaseGuard.Process(raw - offset).Output - expected) <= .001;
    }

    public DriftSnapshot DriftState(PedalAxis axis)
    {
        lock (sync)
        {
            var value = axes[axis];
            if (sensorCalibration is null)
            { return new(false, driftInputError ?? "Waiting for pedal sensor data. Close FEEL-VR Control if it is running.", null, 0, null, null, false); }
            var range = sensorCalibration.Range(axis);
            var reference = sensorCalibration.RawRange(axis);
            var offset = value.Session.BaselineCorrection;
            var status = activeProfile.Axes[axis].CorrectionMode == BaselineCorrectionMode.Auto ? value.Automatic.Status : value.DriftStatus;
            if (!IsFresh(value)) { status = "Waiting for fresh pedal input"; }
            if (reference is null) { status = "Original raw calibration is unavailable. Calibrate this pedal in SherpaAxis first."; }
            if (activeProfile.Axes[axis].CorrectionMode == BaselineCorrectionMode.Off && reference is not null && IsFresh(value))
            { status = "Automatic drift correction is off. Hold Fix drift for 1 second to correct it manually."; }
            var raw = value.Current?.Processing.Input;
            var canFix = reference is not null && IsFresh(value);
            var checking = canFix && (fixRequested == axis || value.ManualCapture || value.BindingReady && value.BindingWasDown && !value.ButtonCaptured);
            var completed = canFix && (value.ManualCaptured || value.ButtonCaptured);
            var settled = value.Session.BaselineCorrection == value.Session.TargetCorrection;
            var stage = checking ? DriftCheckStage.Holding : completed ? DriftCheckStage.Complete : DriftCheckStage.None;
            var manual = activeProfile.Axes[axis].CorrectionMode == BaselineCorrectionMode.ByButton;
            var ready = reference is not null && IsFresh(value) && value.BaselineApproved && settled
                && !value.ManualCapture && !(value.BindingWasDown && !value.ButtonCaptured)
                && Math.Abs(offset) >= .5 && (manual || raw is { } input && Math.Abs(input - (reference.Minimum + offset)) <= (reference.Maximum - (double)reference.Minimum) * .002);
            var saveReason = ready ? "" : !canFix ? status
                : checking ? "Finish the drift check before saving."
                : !settled ? "Wait for the correction to finish before saving."
                : !value.BaselineApproved || Math.Abs(offset) < .5 ? "No drift correction to save."
                : "Take your foot off the pedal before saving.";
            return new(true, status, raw, offset, range.Minimum + offset, range.Maximum + offset, ready, reference?.Minimum, reference?.Maximum,
                canFix, stage, canFix && value.ManualStarted is { } started ? Math.Clamp(elapsed - started, 0, 1) : 0, saveReason);
        }
    }

    public CorrectedCalibration CorrectedHardwareCalibration(PedalAxis axis)
    {
        lock (sync)
        {
            if (!DriftState(axis).CanSave || sensorCalibration is null)
            { throw new InvalidOperationException("Fix drift and wait for the correction to finish before saving."); }
            var shift = checked((int)Math.Round(axes[axis].Session.BaselineCorrection, MidpointRounding.AwayFromZero));
            var original = sensorCalibration.Range(axis);
            var corrected = sensorCalibration.Bounds.WithRange((PedalChannel)axis, new(checked(original.Minimum + shift), checked(original.Maximum + shift)));
            PedalHardwareSettings.Validate(corrected);
            return new(sensorCalibration.SerialNumber, sensorCalibration.Bounds, corrected, axis, sensorCalibration.RawRange(axis)!);
        }
    }
}
