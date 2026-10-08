using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.ReplayChecks;

// Arithmetic oracle: no production processor, session, filter or response-curve evaluation.
internal sealed class ReferenceAxis(AxisProfile profile)
{
    private double? lastTime;
    private int connection;
    private double first;
    private double filtered;
    private readonly List<double> window = [];

    public double Process(double input, double time, int nextConnection)
    {
        var dt = time - lastTime.GetValueOrDefault();
        var reset = !lastTime.HasValue || connection != nextConnection || dt >= (profile.Session?.ResetAfterSeconds ?? .1)
            || dt >= (profile.Filter?.ResetAfterSeconds ?? .1);
        if (profile.Filter is not { } settings) { filtered = input; }
        else if (reset)
        {
            first = filtered = input;
            window.Clear();
            window.Add(input);
        }
        else if (dt > 0)
        {
            if (settings.Kind is SignalFilterKind.Median or SignalFilterKind.MovingAverage or SignalFilterKind.WeightedMovingAverage or SignalFilterKind.MedianExponential)
            {
                window.Add(input);
                if (window.Count > settings.WindowSamples) { window.RemoveAt(0); }
                var sorted = window.Order().ToArray();
                var median = (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
                filtered = settings.Kind switch
                {
                    SignalFilterKind.MovingAverage => window.Average(),
                    SignalFilterKind.WeightedMovingAverage => window.Select((v, i) => v * (i + 1) / (window.Count * (window.Count + 1) / 2d)).Sum(),
                    SignalFilterKind.MedianExponential => median + (filtered - median) * Math.Exp(-dt / settings.TimeConstantSeconds),
                    _ => median
                };
            }
            else
            {
                var retention = Math.Exp(-dt / settings.TimeConstantSeconds);
                first = input + (first - input) * retention;
                filtered = settings.Kind == SignalFilterKind.TwoStageExponential ? first + (filtered - first) * retention : first;
            }
        }
        connection = nextConnection;
        lastTime = time;
        var x = Math.Clamp((filtered - profile.Calibration.Minimum) / profile.Calibration.Span, 0, 1);
        if (profile.Calibration.Inverted) { x = 1 - x; }
        x = Math.Clamp((x - profile.Deadzone.Lower) / (1 - profile.Deadzone.Lower - profile.Deadzone.Upper), 0, 1);
        return Evaluate(profile.Curve, x);
    }

    private static double Evaluate(ProfileCurve curve, double x)
    {
        if (curve.Kind == "linear") { return x; }
        if (curve.Kind == "power") { return Math.Pow(x, curve.Exponent!.Value); }
        var points = curve.Points;
        for (var i = 1; i < points.Count; i++)
        {
            if (x <= points[i].Input)
            {
                var weight = (x - points[i - 1].Input) / (points[i].Input - points[i - 1].Input);
                return points[i - 1].Output + weight * (points[i].Output - points[i - 1].Output);
            }
        }
        return 1;
    }
}
