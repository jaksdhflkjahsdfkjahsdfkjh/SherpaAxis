namespace SherpaAxis.App.Models;

public sealed partial class DashboardEngine
{
    private bool telemetryCapture;
    private long telemetryGeneration;

    internal void SetTelemetryCapture(bool enabled)
    {
        lock (sync)
        {
            if (telemetryCapture == enabled) { return; }
            telemetryCapture = enabled;
            ResetTelemetryHistory();
            if (enabled)
            {
                foreach (var axis in axes.Values)
                {
                    foreach (var sample in axis.History) { AddTelemetry(axis, sample); }
                }
            }
        }
    }

    private void ResetTelemetryHistory()
    {
        telemetryGeneration++;
        foreach (var axis in axes.Values) { axis.Telemetry.Clear(); axis.LastTelemetryTime = null; }
    }

    private void AddTelemetry(AxisState axis, PlotSample sample)
    {
        if (!telemetryCapture) { return; }
        if (axis.LastTelemetryTime is { } last && (sample.Time < last || sample.Time - last > .25))
        { axis.Telemetry.Clear(); telemetryGeneration++; }
        axis.LastTelemetryTime = sample.Time;
        axis.Telemetry.Add(sample);
    }

    internal TelemetryFrame? TelemetrySnapshot(double windowSeconds, long? frozenGeneration = null, int maximumPoints = 1000)
    {
        if (!double.IsFinite(windowSeconds) || windowSeconds is < 1 or > 60) { throw new ArgumentOutOfRangeException(nameof(windowSeconds)); }
        lock (sync)
        {
            if (frozenGeneration == telemetryGeneration) { return null; }
            return new(telemetryGeneration, elapsed, axes.Select(pair => new PedalHistory(pair.Key,
                TelemetryGraph.Window(pair.Value.Telemetry, elapsed - windowSeconds, elapsed, maximumPoints), "")).ToArray());
        }
    }
}
