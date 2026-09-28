namespace SherpaAxis.Core;

public sealed class ResponseCurve
{
    private readonly Func<double, double> evaluate;

    private ResponseCurve(Func<double, double> evaluate) => this.evaluate = evaluate;

    public static ResponseCurve Linear { get; } = new(input => input);

    public static ResponseCurve Power(double exponent)
    {
        if (!double.IsFinite(exponent) || exponent <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exponent));
        }

        return new(input => Math.Pow(input, exponent));
    }

    public static ResponseCurve PiecewiseLinear(IEnumerable<CurvePoint> controlPoints)
    {
        ArgumentNullException.ThrowIfNull(controlPoints);
        var points = controlPoints.ToArray();
        if (points.Length < 2 || points[0] != new CurvePoint(0, 0) || points[^1] != new CurvePoint(1, 1))
        {
            throw new ArgumentException("Curves must begin at (0, 0) and end at (1, 1).", nameof(controlPoints));
        }

        for (var index = 1; index < points.Length; index++)
        {
            var point = points[index];
            var previous = points[index - 1];
            if (!double.IsFinite(point.Input) || !double.IsFinite(point.Output) ||
                point.Input <= previous.Input || point.Input > 1 || point.Output < previous.Output || point.Output > 1)
            {
                throw new ArgumentException("Curve inputs must increase strictly; outputs must be bounded and nondecreasing.", nameof(controlPoints));
            }
        }

        return new(input => Interpolate(points, input));
    }

    public double Apply(double normalizedInput)
    {
        if (!double.IsFinite(normalizedInput))
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedInput));
        }

        return Math.Clamp(evaluate(Math.Clamp(normalizedInput, 0, 1)), 0, 1);
    }

    private static double Interpolate(CurvePoint[] points, double input)
    {
        for (var index = 1; index < points.Length; index++)
        {
            var right = points[index];
            if (input <= right.Input)
            {
                var left = points[index - 1];
                var fraction = (input - left.Input) / (right.Input - left.Input);
                return left.Output + fraction * (right.Output - left.Output);
            }
        }

        return 1;
    }
}
