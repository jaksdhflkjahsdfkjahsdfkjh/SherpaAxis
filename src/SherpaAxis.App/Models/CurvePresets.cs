using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public static class CurvePresets
{
    public static ProfileCurve Create(string name) => name switch
    {
        "Linear" => ProfileCurve.Linear,
        "Progressive" => ProfileCurve.Power(2),
        "Aggressive" => ProfileCurve.Power(.5),
        "S curve" => ProfileCurve.PiecewiseLinear(Enumerable.Range(0, 17).Select(index =>
        {
            var x = index / 16d;
            return new CurvePoint(x, x * x * (3 - 2 * x));
        })),
        "Late bite" => ProfileCurve.PiecewiseLinear([new(0, 0), new(.3, .08), new(.6, .3), new(.8, .6), new(1, 1)]),
        "Early bite" => ProfileCurve.PiecewiseLinear([new(0, 0), new(.2, .4), new(.4, .7), new(.7, .92), new(1, 1)]),
        _ => throw new ArgumentException("Unknown curve preset.", nameof(name))
    };

    public static CurvePoint[] Handles(ProfileCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (curve.Kind == "piecewiseLinear") { return curve.Points.ToArray(); }
        var processor = new AxisProfile(new(0, 1), curve: curve).CreateProcessor();
        return Enumerable.Range(0, 9).Select(i => new CurvePoint(i / 8d, processor.Process(i / 8d).Output)).ToArray();
    }
}
