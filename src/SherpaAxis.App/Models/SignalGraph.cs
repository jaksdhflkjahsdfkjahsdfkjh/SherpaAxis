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
        "Filtered vs curved" => sample.Output,
        "Input" => Input(sample),
        "Device counts" => sample.DeviceInput,
        "Filter difference" => Input(sample) - sample.Filtered,
        _ => sample.Filtered
    };

    public static GraphFrame Prepare(IReadOnlyList<PedalHistory> series, string pedal, string mode, bool zoom, double windowSeconds = 10, double? viewEnd = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0) { throw new ArgumentOutOfRangeException(nameof(windowSeconds)); }
        var selected = series.Where(s => pedal == "All pedals" || s.Axis.ToString() == pedal).ToArray();
        var end = viewEnd ?? selected.SelectMany(s => s.Samples).Select(s => s.Time).DefaultIfEmpty(0).Max();
        if (!double.IsFinite(end)) { throw new ArgumentOutOfRangeException(nameof(viewEnd)); }
        var start = end - windowSeconds;
        var lanes = selected.Select(s =>
        {
            var visible = s.Samples.Where(p => p.Time >= start && p.Time <= end).ToList();
            if (visible.Count == 0 || visible[0].Time > start)
            {
                var before = s.Samples.LastOrDefault(p => p.Time < start);
                var hasBefore = s.Samples.Any(p => p.Time < start);
                visible.Insert(0, hasBefore ? before with { Time = start } : new(start, 0, 0, 0, 0, 0, true));
                if (!hasBefore && visible.Count > 1 && visible[1].Time > start)
                {
                    visible.Insert(1, new(Math.BitDecrement(visible[1].Time), 0, 0, 0, 0, 0, true));
                }
            }
            if (visible[^1].Time < end) { visible.Add(visible[^1] with { Time = end }); }
            var samples = visible.ToArray();
            var minimum = 0d;
            var maximum = 1d;
            if (samples.Length > 0 && (zoom || mode is "Device counts" or "Filter difference"))
            {
                var actual = samples.Where(p => !p.IsPadding).ToArray();
                var rangeSamples = actual.Length > 0 ? actual : samples;
                var values = rangeSamples.Select(p => Value(p, mode));
                if (mode == "Input vs filtered") { values = values.Concat(rangeSamples.Select(Input)); }
                if (mode == "Filtered vs curved") { values = values.Concat(rangeSamples.Select(p => p.Filtered)); }
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
