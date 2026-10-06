namespace SherpaAxis.Core;

internal sealed class AxisSignalFilter(LowPassFilterSettings settings)
{
    private double? previousTime;
    private double value;
    private double firstStage;
    private readonly Queue<double> window = new();

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
            firstStage = input;
            window.Clear();
            window.Enqueue(input);
        }
        else if (interval > 0)
        {
            if (settings.Kind is SignalFilterKind.MovingAverage or SignalFilterKind.Median)
            {
                window.Enqueue(input);
                if (window.Count > settings.WindowSamples)
                {
                    window.Dequeue();
                }
                if (settings.Kind == SignalFilterKind.Median)
                {
                    Span<double> sorted = stackalloc double[window.Count];
                    var index = 0;
                    foreach (var sample in window) { sorted[index++] = sample; }
                    sorted.Sort();
                    value = sorted.Length % 2 == 0
                        ? .5 * sorted[sorted.Length / 2 - 1] + .5 * sorted[sorted.Length / 2]
                        : sorted[sorted.Length / 2];
                }
                else
                {
                    value = 0;
                    foreach (var sample in window) { value += sample / window.Count; }
                    value = Math.Clamp(value, window.Min(), window.Max());
                }
            }
            else
            {
                var ratio = interval / settings.TimeConstantSeconds;
                // Preserve small positive weights when subtracting Exp(-ratio) from one would round to zero.
                var weight = ratio < .001
                    ? ratio * (1 + ratio * (-.5 + ratio * (1d / 6 + ratio * (-1d / 24 + ratio / 120))))
                    : 1 - Math.Exp(-ratio);
                // A convex sum avoids overflow when finite samples have opposite signs.
                firstStage = Blend(firstStage, input, weight);
                value = settings.Kind == SignalFilterKind.TwoStageExponential ? Blend(value, firstStage, weight) : firstStage;
            }
        }

        previousTime = elapsedSeconds;
        return (value, reset);
    }

    public void Reset() => previousTime = null;

    private static double Blend(double previous, double input, double weight) =>
        Math.Clamp((1 - weight) * previous + weight * input, Math.Min(previous, input), Math.Max(previous, input));
}
