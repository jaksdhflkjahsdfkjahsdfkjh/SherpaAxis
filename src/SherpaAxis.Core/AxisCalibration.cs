namespace SherpaAxis.Core;

public sealed class AxisCalibration
{
    public AxisCalibration(double minimum, double maximum, bool inverted = false)
    {
        if (!double.IsFinite(minimum))
        {
            throw new ArgumentOutOfRangeException(nameof(minimum));
        }

        if (!double.IsFinite(maximum) || maximum <= minimum || !double.IsFinite(maximum - minimum))
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), "Calibration must have a finite, positive span.");
        }

        Minimum = minimum;
        Maximum = maximum;
        Inverted = inverted;
    }

    public double Minimum { get; }
    public double Maximum { get; }
    public bool Inverted { get; }
    public double Span => Maximum - Minimum;
    public double ReleasedReference => Inverted ? Maximum : Minimum;

    public double Normalize(double input)
    {
        if (!double.IsFinite(input))
        {
            throw new ArgumentOutOfRangeException(nameof(input));
        }

        var normalized = (Math.Clamp(input, Minimum, Maximum) - Minimum) / Span;
        return Inverted ? 1 - normalized : normalized;
    }
}
