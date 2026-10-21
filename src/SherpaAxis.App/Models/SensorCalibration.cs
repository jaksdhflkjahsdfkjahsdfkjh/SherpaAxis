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
        return new(new(minimum + profile.Calibration.Minimum * scale, minimum + profile.Calibration.Maximum * scale, profile.Calibration.Inverted),
            profile.Deadzone, profile.Curve, profile.MaximumBaselineCorrection * scale, profile.Filter,
            profile.Session is { } session ? Map(axis, session) : null, profile.CorrectionMode, profile.CorrectionButton);
    }
    public AxisSessionSettings Map(PedalAxis axis, AxisSessionSettings settings) => new(
        settings.MaximumCorrectionPerSecond * Scale(axis), settings.MaximumCorrectionOutputPerSecond, settings.ResetAfterSeconds);
}

public sealed record CorrectedCalibration(string SerialNumber, PedalCalibration Original, PedalCalibration Corrected,
    PedalAxis Axis, HardwareCalibrationRange RawOriginal);
public sealed record DriftSnapshot(bool RawAvailable, string Status, double? Input, double Offset, double? Minimum, double? Maximum, bool CanSave,
    double? RawMinimum = null, double? RawMaximum = null);
