namespace SherpaAxis.Core;

public sealed class AxisDeadzone
{
    public AxisDeadzone(double lower = 0, double upper = 0)
    {
        if (!double.IsFinite(lower) || lower is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lower));
        }

        if (!double.IsFinite(upper) || upper is < 0 or >= 1 || lower + upper >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(upper), "Deadzones must leave a positive active range.");
        }

        Lower = lower;
        Upper = upper;
    }

    public double Lower { get; }
    public double Upper { get; }

    public double Apply(double normalizedInput)
    {
        if (!double.IsFinite(normalizedInput))
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedInput));
        }

        return Math.Clamp((normalizedInput - Lower) / (1 - (Lower + Upper)), 0, 1);
    }
}
