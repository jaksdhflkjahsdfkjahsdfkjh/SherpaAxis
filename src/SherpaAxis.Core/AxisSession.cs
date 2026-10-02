namespace SherpaAxis.Core;

public sealed class AxisSession
{
    private readonly AxisCalibration calibration;
    private readonly AxisDeadzone deadzone;
    private readonly ResponseCurve curve;
    private readonly AxisSessionSettings settings;
    private readonly AxisLowPassFilter? filter;
    private double? previousTime;
    private long connection;

    public AxisSession(AxisCalibration calibration, AxisSessionSettings settings, AxisDeadzone? deadzone = null,
        ResponseCurve? curve = null, double maximumBaselineCorrection = 0, LowPassFilterSettings? filterSettings = null)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(settings);
        if (!double.IsFinite(maximumBaselineCorrection) || maximumBaselineCorrection < 0 || maximumBaselineCorrection > calibration.Span)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBaselineCorrection));
        }

        this.calibration = calibration;
        this.settings = settings;
        this.deadzone = deadzone ?? new AxisDeadzone();
        this.curve = curve ?? ResponseCurve.Linear;
        filter = filterSettings is null ? null : new AxisLowPassFilter(filterSettings);
        MaximumBaselineCorrection = maximumBaselineCorrection;
    }

    public double MaximumBaselineCorrection { get; }
    public double BaselineCorrection { get; private set; }
    public double TargetCorrection { get; private set; }

    // Release authorization is supplied separately for every sample; a baseline request does not grant it.
    public void ConfirmReleasedBaseline(double releasedInput)
    {
        if (!previousTime.HasValue)
        {
            throw new InvalidOperationException("Start a session with a sample before requesting correction.");
        }

        var correction = releasedInput - calibration.ReleasedReference;
        if (!double.IsFinite(releasedInput) || !double.IsFinite(correction) || Math.Abs(correction) > MaximumBaselineCorrection)
        {
            throw new ArgumentOutOfRangeException(nameof(releasedInput));
        }

        TargetCorrection = correction;
    }

    public void DisableCompensation() => TargetCorrection = 0;

    public void Reset()
    {
        previousTime = null;
        connection = 0;
        ClearCorrection();
    }

    public AxisSessionResult Process(double input, double elapsedSeconds, long connectionNumber, bool releaseConfirmed = false)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds < previousTime)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }

        if (connectionNumber <= 0 || connectionNumber < connection)
        {
            throw new ArgumentOutOfRangeException(nameof(connectionNumber));
        }

        var normalized = calibration.Normalize(input);
        // Reject possible subtraction overflow before changing any session or filter state.
        _ = calibration.Normalize(input - MaximumBaselineCorrection);
        _ = calibration.Normalize(input + MaximumBaselineCorrection);
        var dt = elapsedSeconds - previousTime.GetValueOrDefault(elapsedSeconds);
        var reset = !previousTime.HasValue ? AxisSessionResetReason.Started
            : connection != connectionNumber ? AxisSessionResetReason.ConnectionChanged
            : dt >= settings.ResetAfterSeconds ? AxisSessionResetReason.SamplingGap
            : AxisSessionResetReason.None;
        if (reset != AxisSessionResetReason.None)
        {
            ClearCorrection();
            dt = 0;
        }

        var (filteredRaw, filterReset) = filter?.Apply(input, elapsedSeconds) ?? (input, false);
        var oldOutput = Output(filteredRaw, BaselineCorrection);
        if (releaseConfirmed && dt > 0 && BaselineCorrection != TargetCorrection)
        {
            var amount = settings.MaximumCorrectionPerSecond * dt;
            var candidate = TargetCorrection > BaselineCorrection
                ? Math.Min(TargetCorrection, BaselineCorrection + amount)
                : Math.Max(TargetCorrection, BaselineCorrection - amount);
            BaselineCorrection = LimitOutputStep(filteredRaw, candidate, oldOutput,
                settings.MaximumCorrectionOutputPerSecond * dt);
        }

        var correctedInput = input - BaselineCorrection;
        var filteredInput = filteredRaw - BaselineCorrection;
        var filteredNormalized = calibration.Normalize(filteredInput);
        var afterDeadzone = deadzone.Apply(filteredNormalized);
        var output = curve.Apply(afterDeadzone);
        var processing = new AxisProcessingResult(input, correctedInput, normalized,
            calibration.Normalize(correctedInput), afterDeadzone, output, BaselineCorrection,
            filteredInput, filteredNormalized, filterReset);
        previousTime = elapsedSeconds;
        connection = connectionNumber;
        return new(processing, TargetCorrection, releaseConfirmed,
            !releaseConfirmed && BaselineCorrection != TargetCorrection, output - oldOutput, reset);
    }

    private double Output(double input, double correction) => curve.Apply(deadzone.Apply(calibration.Normalize(input - correction)));

    private double LimitOutputStep(double input, double candidate, double oldOutput, double maximumChange)
    {
        if (Math.Abs(Output(input, candidate) - oldOutput) <= maximumChange)
        {
            return candidate;
        }

        // Monotonic calibration/deadzone/curve makes the passing interval contiguous, including plateaus.
        var lower = 0d;
        var upper = 1d;
        var accepted = BaselineCorrection;
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var fraction = (lower + upper) / 2;
            var correction = Math.Sign(candidate) == Math.Sign(BaselineCorrection)
                ? BaselineCorrection + fraction * (candidate - BaselineCorrection)
                : (1 - fraction) * BaselineCorrection + fraction * candidate;
            if (Math.Abs(Output(input, correction) - oldOutput) <= maximumChange)
            {
                lower = fraction;
                accepted = correction;
            }
            else
            {
                upper = fraction;
            }
        }

        return accepted;
    }

    private void ClearCorrection()
    {
        BaselineCorrection = 0;
        TargetCorrection = 0;
        filter?.Reset();
    }
}
