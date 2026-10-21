namespace SherpaAxis.App.Models;

internal sealed class StableReleaseWindow
{
    internal const double MaximumVariationFraction = .002;
    private double? started;
    private double low, high;
    internal double StableSeconds { get; private set; }
    internal double Variation => high - low;
    internal void Reset() { started = null; StableSeconds = 0; }

    internal double? Observe(double input, double time, double span)
    {
        if (started is null) { started = time; low = high = input; }
        low = Math.Min(low, input);
        high = Math.Max(high, input);
        if (high - low > span * MaximumVariationFraction)
        {
            started = time; low = high = input; StableSeconds = 0;
            return null;
        }
        StableSeconds = Math.Min(1, time - started.Value);
        // Keep observed released jitter below the corrected zero, including steep response curves.
        return time - started.Value >= 1 ? high : null;
    }
}
