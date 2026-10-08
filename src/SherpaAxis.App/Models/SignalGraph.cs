using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed record GraphLane(PedalAxis Axis, string FilterLabel, IReadOnlyList<PlotSample> Samples, double Minimum, double Maximum);
public sealed record GraphFrame(double Start, double End, IReadOnlyList<GraphLane> Lanes);

public static class SignalGraph
{
    public static double Input(PlotSample sample) => double.IsFinite(sample.PreFilter) ? sample.PreFilter : sample.Input;
    public static double Value(PlotSample sample, string mode) => mode switch
    {
        "Output" => sample.Output,
        "Input" => Input(sample),
        "Device counts" => sample.DeviceInput,
        "Filter difference" => Input(sample) - sample.Filtered,
        _ => sample.Filtered
    };

    public static GraphFrame Prepare(IReadOnlyList<PedalHistory> series, string pedal, string mode, bool zoom, double windowSeconds = 12)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0) { throw new ArgumentOutOfRangeException(nameof(windowSeconds)); }
        var selected = series.Where(s => pedal == "All pedals" || s.Axis.ToString() == pedal).ToArray();
        var end = selected.SelectMany(s => s.Samples).Select(s => s.Time).DefaultIfEmpty(0).Max();
        var first = selected.SelectMany(s => s.Samples).Select(s => s.Time).DefaultIfEmpty(0).Min();
        var start = Math.Max(first, end - windowSeconds);
        var lanes = selected.Select(s =>
        {
            var samples = s.Samples.Where(p => p.Time >= start).ToArray();
            var minimum = 0d;
            var maximum = 1d;
            if (samples.Length > 0 && (zoom || mode is "Device counts" or "Filter difference"))
            {
                var values = samples.Select(p => Value(p, mode));
                if (mode == "Input vs filtered") { values = values.Concat(samples.Select(Input)); }
                minimum = values.Min();
                maximum = values.Max();
                if (mode == "Filter difference")
                {
                    maximum = Math.Max(.0001, Math.Max(Math.Abs(minimum), Math.Abs(maximum)) * 1.1);
                    minimum = -maximum;
                }
                else
                {
                    var padding = Math.Max(mode == "Device counts" ? 2 : .005, (maximum - minimum) * .1);
                    minimum -= padding;
                    maximum += padding;
                    if (mode != "Device counts") { minimum = Math.Max(0, minimum); maximum = Math.Min(1, maximum); }
                }
            }
            return new GraphLane(s.Axis, s.FilterLabel, samples, minimum, maximum);
        }).ToArray();
        return new(start, end, lanes);
    }
}
