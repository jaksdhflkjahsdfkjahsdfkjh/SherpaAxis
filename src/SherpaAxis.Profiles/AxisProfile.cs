using SherpaAxis.Core;

namespace SherpaAxis.Profiles;

public sealed class AxisProfile
{
    public AxisProfile(AxisCalibration calibration, AxisDeadzone? deadzone = null, ProfileCurve? curve = null,
        double maximumBaselineCorrection = 0, LowPassFilterSettings? filter = null, AxisSessionSettings? session = null,
        BaselineCorrectionMode correctionMode = BaselineCorrectionMode.Off, CorrectionButton? correctionButton = null,
        ClutchBitePointSettings? bitePoint = null)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        Calibration = calibration;
        Deadzone = deadzone ?? new AxisDeadzone();
        Curve = curve ?? ProfileCurve.Linear;
        MaximumBaselineCorrection = maximumBaselineCorrection;
        Filter = filter;
        Session = session;
        if (!Enum.IsDefined(correctionMode)) { throw new ArgumentOutOfRangeException(nameof(correctionMode)); }
        CorrectionMode = correctionMode;
        CorrectionButton = correctionButton;
        BitePoint = bitePoint;
        _ = CreateProcessor();
    }

    public AxisCalibration Calibration { get; }
    public AxisDeadzone Deadzone { get; }
    public ProfileCurve Curve { get; }
    public double MaximumBaselineCorrection { get; }
    public LowPassFilterSettings? Filter { get; }
    public AxisSessionSettings? Session { get; }
    public BaselineCorrectionMode CorrectionMode { get; }
    public CorrectionButton? CorrectionButton { get; }
    public ClutchBitePointSettings? BitePoint { get; }

    public AxisProfile WithBitePoint(ClutchBitePointSettings? settings) => new(Calibration, Deadzone, Curve,
        MaximumBaselineCorrection, Filter, Session, CorrectionMode, CorrectionButton, settings);

    public AxisProcessor CreateProcessor() => new(Calibration, Deadzone, Curve.Curve, MaximumBaselineCorrection, Filter);

    public AxisSession CreateSession(AxisSessionSettings settings) => new(Calibration, settings, Deadzone, Curve.Curve,
        MaximumBaselineCorrection, Filter);
}
