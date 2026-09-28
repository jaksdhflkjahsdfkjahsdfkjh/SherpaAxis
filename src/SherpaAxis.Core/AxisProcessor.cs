namespace SherpaAxis.Core;

public sealed class AxisProcessor
{
    private readonly AxisCalibration calibration;
    private readonly AxisDeadzone deadzone;
    private readonly ResponseCurve curve;

    public AxisProcessor(AxisCalibration calibration, AxisDeadzone? deadzone = null,
        ResponseCurve? curve = null, double maximumBaselineCorrection = 0)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (!double.IsFinite(maximumBaselineCorrection) || maximumBaselineCorrection < 0 || maximumBaselineCorrection > calibration.Span)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBaselineCorrection));
        }

        this.calibration = calibration;
        this.deadzone = deadzone ?? new AxisDeadzone();
        this.curve = curve ?? ResponseCurve.Linear;
        MaximumBaselineCorrection = maximumBaselineCorrection;
    }

    public double MaximumBaselineCorrection { get; }
    public double BaselineCorrection { get; private set; }

    // The caller must obtain an explicit released-pedal confirmation; samples cannot establish release.
    public void ConfirmReleasedBaseline(double releasedInput)
    {
        var correction = releasedInput - calibration.ReleasedReference;
        if (!double.IsFinite(releasedInput) || !double.IsFinite(correction) || Math.Abs(correction) > MaximumBaselineCorrection)
        {
            throw new ArgumentOutOfRangeException(nameof(releasedInput), "The requested baseline exceeds the configured correction limit.");
        }

        BaselineCorrection = correction;
    }

    public void ResetBaseline() => BaselineCorrection = 0;

    public AxisProcessingResult Process(double input)
    {
        var normalized = calibration.Normalize(input);
        var correctedInput = input - BaselineCorrection;
        var correctedNormalized = calibration.Normalize(correctedInput);
        var afterDeadzone = deadzone.Apply(correctedNormalized);
        return new(input, correctedInput, normalized, correctedNormalized, afterDeadzone,
            curve.Apply(afterDeadzone), BaselineCorrection);
    }
}
