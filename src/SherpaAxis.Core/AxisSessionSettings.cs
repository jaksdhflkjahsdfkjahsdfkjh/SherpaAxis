namespace SherpaAxis.Core;

public sealed class AxisSessionSettings
{
    public AxisSessionSettings(double maximumCorrectionPerSecond, double maximumCorrectionOutputPerSecond,
        double resetAfterSeconds = .1)
    {
        if (!double.IsFinite(maximumCorrectionPerSecond) || maximumCorrectionPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCorrectionPerSecond));
        }

        if (!double.IsFinite(maximumCorrectionOutputPerSecond) || maximumCorrectionOutputPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCorrectionOutputPerSecond));
        }

        if (!double.IsFinite(resetAfterSeconds) || resetAfterSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resetAfterSeconds));
        }

        MaximumCorrectionPerSecond = maximumCorrectionPerSecond;
        MaximumCorrectionOutputPerSecond = maximumCorrectionOutputPerSecond;
        ResetAfterSeconds = resetAfterSeconds;
    }

    public double MaximumCorrectionPerSecond { get; }
    public double MaximumCorrectionOutputPerSecond { get; }
    public double ResetAfterSeconds { get; }
}
