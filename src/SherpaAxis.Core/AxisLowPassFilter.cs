namespace SherpaAxis.Core;

internal sealed class AxisLowPassFilter(LowPassFilterSettings settings)
{
    private double? previousTime;
    private double value;

    public (double Value, bool Reset) Apply(double input, double elapsedSeconds)
    {
        if (previousTime.HasValue && elapsedSeconds < previousTime.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Filter timestamps must be nondecreasing.");
        }

        var interval = elapsedSeconds - previousTime.GetValueOrDefault();
        var reset = !previousTime.HasValue || interval >= settings.ResetAfterSeconds;
        if (reset)
        {
            value = input;
        }
        else if (interval > 0)
        {
            var ratio = interval / settings.TimeConstantSeconds;
            // Preserve small positive weights when subtracting Exp(-ratio) from one would round to zero.
            var weight = ratio < .001
                ? ratio * (1 + ratio * (-.5 + ratio * (1d / 6 + ratio * (-1d / 24 + ratio / 120))))
                : 1 - Math.Exp(-ratio);
            // A convex sum avoids overflow when finite samples have opposite signs.
            var next = (1 - weight) * value + weight * input;
            value = Math.Clamp(next, Math.Min(value, input), Math.Max(value, input));
        }

        previousTime = elapsedSeconds;
        return (value, reset);
    }

    public void Reset() => previousTime = null;
}
