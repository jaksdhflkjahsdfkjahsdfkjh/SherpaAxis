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
            axes[axis].DriftStatus = "Release the pedal completely and keep your foot off while the baseline is checked";
        }
    }

    public void CancelFixDrift(PedalAxis axis)
    {
        lock (sync)
        {
            if (fixRequested == axis) { fixRequested = null; }
            var values = axes[axis];
            if (!values.ManualCapture && !values.ManualAdjusting) { return; }
            values.ManualCapture = values.ManualAdjusting = false;
            values.ReleaseWindow.Reset();
            values.ManualStarted = null;
            values.DriftStatus = values.BaselineApproved && values.Session.BaselineCorrection == values.Session.TargetCorrection
                ? "Drift fixed for this session. Save corrected calibration to keep it in the controller"
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
                values.DriftStatus = "Drift capture expired; release the pedal and try again";
                return false;
            }
            values.ManualCapture = true; values.ManualAdjusting = false; values.ManualStarted = elapsed;
            values.ReleaseWindow.Reset(); fixRequested = null;
        }
        var held = values.BindingReady && values.BindingWasDown && values.AuthorizedAt is { } authorized
            && clock.GetElapsedTime(authorized) < TimeSpan.FromMilliseconds(150);
        if (!values.ManualCapture && !values.ManualAdjusting && !held)
        {
            if (values.BindingWasDown)
            {
                values.BindingWasDown = values.BindingReady = false; values.AuthorizedAt = null;
                values.ManualStarted = null; values.ReleaseWindow.Reset();
                values.DriftStatus = "Drift check stopped; release the bound button before trying again";
            }
            return false;
        }
        if (held && !values.ButtonCaptured && !values.ManualAdjusting) { values.ManualStarted ??= elapsed; }
        if ((values.ManualCapture || held && !values.ButtonCaptured) && elapsed - values.ManualStarted > 10)
        {
            values.ManualCapture = false;
            values.BindingReady = values.BindingWasDown = false; values.AuthorizedAt = null; values.ManualStarted = null;
            values.DriftStatus = "Baseline was not stable for one second; no new correction";
            values.ReleaseWindow.Reset(); return false;
        }
        if ((values.ButtonCaptured || values.ManualAdjusting) && values.ManualCandidate is { } target)
        {
            if (Math.Abs(raw - target) > physical.Span * StableReleaseWindow.MaximumVariationFraction)
            {
                values.ManualAdjusting = false; values.ButtonCaptured = false; values.ReleaseWindow.Reset();
                values.BindingReady = false; values.AuthorizedAt = null;
                values.DriftStatus = "Pedal moved; correction paused. Release and capture again";
                return false;
            }
            return true;
        }
        var captured = values.ReleaseWindow.Observe(raw, elapsed, physical.Span);
        values.DriftStatus = FormattableString.Invariant($"Checking released baseline: {values.ReleaseWindow.StableSeconds:F1}/1.0 s stable. Keep your foot off");
        if (captured is not { } released) { return false; }
        var correction = released - physical.Minimum;
        if (Math.Abs(correction) > Math.Min(maximum, physical.Span * .25) || !ValidShift(axis, correction))
        {
            values.ManualCapture = false; values.ReleaseWindow.Reset(); values.BindingReady = false;
            values.DriftStatus = "Drift exceeds the configured correction limit or sensor range; full calibration is required";
            return false;
        }
        values.Session.ConfirmReleasedBaseline(ProfileForInput(axis, settings).Calibration.ReleasedReference + correction);
        values.ManualCandidate = released;
        values.ManualAdjusting = values.ManualCapture;
        values.ManualCapture = false;
        values.ButtonCaptured = held;
        values.BaselineApproved = true;
        values.DriftStatus = Math.Abs(correction) < .5 ? "Released baseline matches calibration; no correction needed"
            : "Stable released baseline accepted; correction is temporary until saved to the controller";
        return true;
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
            { return new(false, driftInputError ?? "Drift monitoring needs raw sensor input. Select Auto or By button; close FEEL-VR Control", null, 0, null, null, false); }
            var range = sensorCalibration.Range(axis);
            var reference = sensorCalibration.RawRange(axis);
            var offset = value.Session.BaselineCorrection;
            var status = activeProfile.Axes[axis].CorrectionMode == BaselineCorrectionMode.Auto ? value.Automatic.Status : value.DriftStatus;
            if (!IsFresh(value)) { status = "Waiting for fresh pedal input"; }
            if (reference is null) { status = "Original raw calibration is unavailable. Calibrate this pedal in SherpaAxis first."; }
            if (activeProfile.Axes[axis].CorrectionMode == BaselineCorrectionMode.Off) { status = "Baseline adjustment is off. Hardware calibration remains unchanged."; }
            var raw = value.Current?.Processing.Input;
            var ready = reference is not null && IsFresh(value) && value.BaselineApproved && value.Session.BaselineCorrection == value.Session.TargetCorrection
                && !value.ManualCapture && !value.ManualAdjusting && !(value.BindingWasDown && !value.ButtonCaptured)
                && Math.Abs(offset) >= .5 && raw is { } input && Math.Abs(input - (reference.Minimum + offset)) <= (reference.Maximum - (double)reference.Minimum) * .002;
            return new(true, status, raw, offset, range.Minimum + offset, range.Maximum + offset, ready, reference?.Minimum, reference?.Maximum);
        }
    }

    public CorrectedCalibration CorrectedHardwareCalibration(PedalAxis axis)
    {
        lock (sync)
        {
            if (!DriftState(axis).CanSave || sensorCalibration is null)
            { throw new InvalidOperationException("Fix drift with the pedal fully released and wait for correction to settle before saving."); }
            var shift = checked((int)Math.Round(axes[axis].Session.BaselineCorrection, MidpointRounding.AwayFromZero));
            var original = sensorCalibration.Range(axis);
            var corrected = sensorCalibration.Bounds.WithRange((PedalChannel)axis, new(checked(original.Minimum + shift), checked(original.Maximum + shift)));
            PedalHardwareSettings.Validate(corrected);
            return new(sensorCalibration.SerialNumber, sensorCalibration.Bounds, corrected, axis, sensorCalibration.RawRange(axis)!);
        }
    }
}
