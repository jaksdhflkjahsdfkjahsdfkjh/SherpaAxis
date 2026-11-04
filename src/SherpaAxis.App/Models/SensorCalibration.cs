using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed record SensorCalibration(string SerialNumber, PedalCalibration Bounds,
    IReadOnlyDictionary<PedalAxis, HardwareCalibrationRange>? RawRanges = null)
{
    public HardwareCalibrationRange Range(PedalAxis axis) => Bounds.Range((PedalChannel)axis);
    public double Scale(PedalAxis axis) => (Range(axis).Maximum - (double)Range(axis).Minimum) / DashboardEngine.DeviceMaximum(axis);
    public HardwareCalibrationRange? RawRange(PedalAxis axis) => RawRanges is not null && RawRanges.TryGetValue(axis, out var raw) ? raw : null;
    public AxisCalibration PhysicalAxis(PedalAxis axis) => RawRange(axis) is { } raw
        ? new(raw.Minimum, raw.Maximum) : throw new InvalidOperationException("Original raw calibration is unavailable. Calibrate this pedal in SherpaAxis first.");
    public AxisProfile Map(PedalAxis axis, AxisProfile profile)
    {
        var scale = Scale(axis);
        var minimum = Range(axis).Minimum;
        var calibration = new AxisCalibration(minimum + profile.Calibration.Minimum * scale, minimum + profile.Calibration.Maximum * scale, profile.Calibration.Inverted);
        // An explicit foot-off check uses raw travel; automatic correction retains the profile limit.
        var maximumCorrection = profile.CorrectionMode == BaselineCorrectionMode.ByButton && RawRange(axis) is { } raw
            ? Math.Min(calibration.Span, (raw.Maximum - (double)raw.Minimum) * .5)
            : profile.MaximumBaselineCorrection * scale;
        return new(calibration,
            profile.Deadzone, profile.Curve, maximumCorrection, profile.Filter,
            profile.Session is { } session ? Map(axis, session) : null, profile.CorrectionMode, profile.CorrectionButton, profile.BitePoint);
    }
    public AxisSessionSettings Map(PedalAxis axis, AxisSessionSettings settings) => new(
        settings.MaximumCorrectionPerSecond * Scale(axis), settings.MaximumCorrectionOutputPerSecond, settings.ResetAfterSeconds);
}

public sealed record CorrectedCalibration(string SerialNumber, PedalCalibration Original, PedalCalibration Corrected,
    PedalAxis Axis, HardwareCalibrationRange RawOriginal);
public sealed record DriftSnapshot(bool RawAvailable, string Status, double? Input, double Offset, double? Minimum, double? Maximum, bool CanSave,
    double? RawMinimum = null, double? RawMaximum = null);
