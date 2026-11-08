using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

internal sealed record TelemetryFrame(long Generation, double End, IReadOnlyList<PedalHistory> Series);

internal static class TelemetryGraph
{
    // Retain extrema for every stage, rather than dropping short spikes when reducing a long time window.
    internal static PlotSample[] Window(IEnumerable<PlotSample> source, double start, double end, int maximum = 1000)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start || maximum < 16) { throw new ArgumentOutOfRangeException(nameof(start)); }
        if (source.TryGetNonEnumeratedCount(out var count) && count <= maximum) { return source.Where(sample => sample.Time >= start && sample.Time <= end).ToArray(); }
        var result = new List<PlotSample>(maximum);
        var buckets = (maximum - 2) / 8;
        var bucket = -1;
        var candidates = new PlotSample[8];
        var populated = false;
        foreach (var sample in source)
        {
            if (sample.Time < start || sample.Time > end) { continue; }
            var index = Math.Min(buckets - 1, (int)((sample.Time - start) / (end - start) * buckets));
            if (index != bucket)
            {
                Flush(); bucket = index;
                Array.Fill(candidates, sample); populated = true;
            }
            ObserveExtrema(candidates, sample);
        }
        Flush();
        return result.ToArray();

        void Flush()
        {
            if (!populated) { return; }
            foreach (var point in candidates.Distinct().OrderBy(point => point.Time)) { result.Add(point); }
        }
    }

    internal static void ObserveExtrema(PlotSample[] candidates, PlotSample sample)
    {
        candidates[7] = sample;
        if (SignalGraph.Input(sample) < SignalGraph.Input(candidates[1])) { candidates[1] = sample; }
        if (SignalGraph.Input(sample) > SignalGraph.Input(candidates[2])) { candidates[2] = sample; }
        if (sample.Filtered < candidates[3].Filtered) { candidates[3] = sample; }
        if (sample.Filtered > candidates[4].Filtered) { candidates[4] = sample; }
        if (sample.Output < candidates[5].Output) { candidates[5] = sample; }
        if (sample.Output > candidates[6].Output) { candidates[6] = sample; }
    }
}
