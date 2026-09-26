using System.Globalization;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Cli;

internal sealed class CaptureStatistics
{
    private long connectionSamples;
    private TimeSpan firstSample;
    private TimeSpan lastDisplay = TimeSpan.MinValue;
    private PedalAxes minimum = new(int.MaxValue, int.MaxValue, int.MaxValue);
    private PedalAxes maximum;

    public long AcquiredSamples { get; private set; }

    public string? Observe(PedalMonitorEvent update)
    {
        if (update.Axes is not { } axes)
        {
            connectionSamples = 0;
            minimum = new PedalAxes(int.MaxValue, int.MaxValue, int.MaxValue);
            maximum = default;
            lastDisplay = TimeSpan.MinValue;
            return string.Create(CultureInfo.InvariantCulture,
                $"{update.Elapsed.TotalSeconds:F2}s {update.State} connection={update.ConnectionNumber}: {update.Detail}");
        }

        if (connectionSamples++ == 0)
        {
            firstSample = update.Elapsed;
        }

        AcquiredSamples++;
        minimum = new PedalAxes(Math.Min(minimum.Throttle, axes.Throttle),
            Math.Min(minimum.Brake, axes.Brake), Math.Min(minimum.Clutch, axes.Clutch));
        maximum = new PedalAxes(Math.Max(maximum.Throttle, axes.Throttle),
            Math.Max(maximum.Brake, axes.Brake), Math.Max(maximum.Clutch, axes.Clutch));
        if (lastDisplay != TimeSpan.MinValue && update.Elapsed - lastDisplay < TimeSpan.FromMilliseconds(250))
        {
            return null;
        }

        var elapsed = (update.Elapsed - firstSample).TotalSeconds;
        var rate = elapsed > 0 ? (connectionSamples - 1) / elapsed : 0;
        lastDisplay = update.Elapsed;
        return string.Create(CultureInfo.InvariantCulture,
            $"{update.Elapsed.TotalSeconds:F2}s connection={update.ConnectionNumber} throttle={axes.Throttle} brake={axes.Brake} clutch={axes.Clutch} delivered={rate:F1}/s min={minimum} max={maximum}");
    }
}
