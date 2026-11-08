using System.Collections;

namespace SherpaAxis.App.Models;

internal sealed class TelemetryBuffer : IEnumerable<PlotSample>
{
    private readonly Queue<PlotSample> samples = new();
    private readonly PlotSample[] pending = new PlotSample[8];
    private bool populated;

    internal void Add(PlotSample sample)
    {
        if (populated && sample.Time - pending[0].Time >= .02)
        {
            foreach (var point in pending.Distinct().OrderBy(point => point.Time)) { samples.Enqueue(point); }
            populated = false;
        }
        if (!populated) { Array.Fill(pending, sample); populated = true; }
        TelemetryGraph.ObserveExtrema(pending, sample);
        while (samples.Count > 24000 || samples.TryPeek(out var first) && sample.Time - first.Time > 60.1) { samples.Dequeue(); }
    }

    internal void Clear() { samples.Clear(); populated = false; }

    public IEnumerator<PlotSample> GetEnumerator()
    {
        foreach (var sample in samples) { yield return sample; }
        if (populated)
        {
            foreach (var sample in pending.Distinct().OrderBy(point => point.Time)) { yield return sample; }
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
