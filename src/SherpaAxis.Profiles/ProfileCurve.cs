using SherpaAxis.Core;

namespace SherpaAxis.Profiles;

public sealed class ProfileCurve
{
    private ProfileCurve(string kind, ResponseCurve curve, double? exponent = null, IReadOnlyList<CurvePoint>? points = null)
    {
        Kind = kind;
        Curve = curve;
        Exponent = exponent;
        Points = points ?? [];
    }

    public const int MaximumControlPoints = 1024;

    public static ProfileCurve Linear { get; } = new("linear", ResponseCurve.Linear);
    public string Kind { get; }
    public double? Exponent { get; }
    public IReadOnlyList<CurvePoint> Points { get; }
    internal ResponseCurve Curve { get; }

    public static ProfileCurve Power(double exponent) => new("power", ResponseCurve.Power(exponent), exponent);

    public static ProfileCurve PiecewiseLinear(IEnumerable<CurvePoint> controlPoints)
    {
        ArgumentNullException.ThrowIfNull(controlPoints);
        var points = controlPoints.Take(MaximumControlPoints + 1).ToArray();
        if (points.Length > MaximumControlPoints)
        {
            throw new ArgumentException($"Profiles support at most {MaximumControlPoints} control points per curve.", nameof(controlPoints));
        }

        var curve = ResponseCurve.PiecewiseLinear(points);
        return new("piecewiseLinear", curve, points: Array.AsReadOnly(points));
    }
}
