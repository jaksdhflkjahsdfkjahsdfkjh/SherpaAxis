using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public static class CurveEditing
{
    public static CurvePoint[] Move(IReadOnlyList<CurvePoint> points, int index, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (!double.IsFinite(x) || !double.IsFinite(y)) { throw new ArgumentOutOfRangeException(nameof(x)); }
        var result = points.ToArray();
        if (index <= 0 || index >= result.Length - 1) { return result; }
        var left = result[index - 1];
        var right = result[index + 1];
        var margin = Math.Min(.001, (right.Input - left.Input) / 3);
        var minimum = Math.Max(left.Input + margin, Math.BitIncrement(left.Input));
        var maximum = Math.Min(right.Input - margin, Math.BitDecrement(right.Input));
        result[index] = new(Math.Clamp(x, minimum, maximum), Math.Clamp(y, left.Output, right.Output));
        return result;
    }

    public static CurvePoint[] Insert(IReadOnlyList<CurvePoint> points, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (!double.IsFinite(x) || !double.IsFinite(y)) { throw new ArgumentOutOfRangeException(nameof(x)); }
        if (points.Count >= ProfileCurve.MaximumControlPoints || x <= .001 || x >= .999 || points.Any(p => Math.Abs(p.Input - x) < .001))
        {
            return points.ToArray();
        }
        var right = 1;
        while (right < points.Count && points[right].Input < x) { right++; }
        var result = points.ToList();
        result.Insert(right, new(x, Math.Clamp(y, points[right - 1].Output, points[right].Output)));
        return result.ToArray();
    }

    public static CurvePoint[] Remove(IReadOnlyList<CurvePoint> points, int index)
    {
        ArgumentNullException.ThrowIfNull(points);
        return points.Where((_, i) => i != index || i == 0 || i == points.Count - 1).ToArray();
    }
}
