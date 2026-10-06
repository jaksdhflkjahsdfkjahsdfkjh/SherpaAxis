namespace SherpaAxis.Core;

public sealed class AxisProcessor
{
    private readonly AxisCalibration calibration;
    private readonly AxisDeadzone deadzone;
    private readonly ResponseCurve curve;
    private readonly AxisSignalFilter? filter;

    public AxisProcessor(AxisCalibration calibration, AxisDeadzone? deadzone = null,
        ResponseCurve? curve = null, double maximumBaselineCorrection = 0, LowPassFilterSettings? filterSettings = null)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (!double.IsFinite(maximumBaselineCorrection) || maximumBaselineCorrection < 0 || maximumBaselineCorrection > calibration.Span)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBaselineCorrection));
        }

        this.calibration = calibration;
        this.deadzone = deadzone ?? new AxisDeadzone();
        this.curve = curve ?? ResponseCurve.Linear;
        filter = filterSettings is null ? null : new AxisSignalFilter(filterSettings);
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
        filter?.Reset();
    }

    public void ResetBaseline()
    {
        BaselineCorrection = 0;
        filter?.Reset();
    }

    public AxisProcessingResult Process(double input)
    {
        if (filter is not null)
        {
            throw new InvalidOperationException("Filtering requires a monotonic elapsed timestamp for each sample.");
        }

        return ProcessCore(input, 0);
    }

    public AxisProcessingResult Process(double input, double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }

        return ProcessCore(input, elapsedSeconds);
    }

    private AxisProcessingResult ProcessCore(double input, double elapsedSeconds)
    {
        var normalized = calibration.Normalize(input);
        var correctedInput = input - BaselineCorrection;
        var correctedNormalized = calibration.Normalize(correctedInput);
        var (filteredInput, filterReset) = filter?.Apply(correctedInput, elapsedSeconds) ?? (correctedInput, false);
        var filteredNormalized = calibration.Normalize(filteredInput);
        var afterDeadzone = deadzone.Apply(filteredNormalized);
        return new(input, correctedInput, normalized, correctedNormalized, afterDeadzone,
            curve.Apply(afterDeadzone), BaselineCorrection, filteredInput, filteredNormalized, filterReset);
    }
}
