namespace SherpaAxis.Core;

public sealed class LowPassFilterSettings
{
    public LowPassFilterSettings(double timeConstantSeconds, double resetAfterSeconds = .1)
    {
        if (!double.IsFinite(timeConstantSeconds) || timeConstantSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeConstantSeconds));
        }

        if (!double.IsFinite(resetAfterSeconds) || resetAfterSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resetAfterSeconds));
        }

        TimeConstantSeconds = timeConstantSeconds;
        ResetAfterSeconds = resetAfterSeconds;
    }

    public double TimeConstantSeconds { get; }
    public double ResetAfterSeconds { get; }
}
