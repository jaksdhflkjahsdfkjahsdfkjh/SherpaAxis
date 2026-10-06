namespace SherpaAxis.Core;

public sealed class LowPassFilterSettings
{
    public LowPassFilterSettings(double timeConstantSeconds, double resetAfterSeconds = .1,
        SignalFilterKind kind = SignalFilterKind.Exponential, int windowSamples = 5)
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
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (windowSamples < 3 || windowSamples > 101 || windowSamples % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSamples), "Window must be odd, between 3 and 101 samples.");
        }
        Kind = kind;
        WindowSamples = windowSamples;
    }

    public double TimeConstantSeconds { get; }
    public double ResetAfterSeconds { get; }
    public SignalFilterKind Kind { get; }
    public int WindowSamples { get; }
}
