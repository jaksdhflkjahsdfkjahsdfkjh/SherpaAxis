using SherpaAxis.Core;

namespace SherpaAxis.Profiles;

public sealed class AxisProfile
{
    public AxisProfile(AxisCalibration calibration, AxisDeadzone? deadzone = null, ProfileCurve? curve = null,
        double maximumBaselineCorrection = 0, LowPassFilterSettings? filter = null)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        Calibration = calibration;
        Deadzone = deadzone ?? new AxisDeadzone();
        Curve = curve ?? ProfileCurve.Linear;
        MaximumBaselineCorrection = maximumBaselineCorrection;
        Filter = filter;
        _ = CreateProcessor();
    }

    public AxisCalibration Calibration { get; }
    public AxisDeadzone Deadzone { get; }
    public ProfileCurve Curve { get; }
    public double MaximumBaselineCorrection { get; }
    public LowPassFilterSettings? Filter { get; }

    public AxisProcessor CreateProcessor() => new(Calibration, Deadzone, Curve.Curve, MaximumBaselineCorrection, Filter);
}
