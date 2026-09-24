using System.Globalization;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Cli;

internal static class AxisCapture
{
    public static async Task<long> RunAsync(PedalMonitor monitor, MonitorOptions options, CancellationToken cancellationToken)
    {
        await using var csv = options.CsvPath is null ? null : new StreamWriter(
            new FileStream(options.CsvPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        if (csv is not null)
        {
            await csv.WriteLineAsync("elapsed_seconds,connection,throttle_device_axis,brake_device_axis,clutch_device_axis").ConfigureAwait(false);
        }

        long totalCount = 0;
        long connectionCount = 0;
        var firstSample = TimeSpan.Zero;
        var lastDisplay = TimeSpan.MinValue;
        var minimum = new PedalAxes(int.MaxValue, int.MaxValue, int.MaxValue);
        var maximum = new PedalAxes(0, 0, 0);
        Console.Error.WriteLine("Firmware-calibrated device axes; ADC RAW is unavailable. No device writes.");
        try
        {
            await foreach (var update in monitor.WatchAsync(options.DevicePath, cancellationToken).ConfigureAwait(false))
            {
                if (update.Axes is not { } axes)
                {
                    Console.Error.WriteLine($"{update.State}: {update.Detail}");
                    connectionCount = 0;
                    minimum = new PedalAxes(int.MaxValue, int.MaxValue, int.MaxValue);
                    maximum = new PedalAxes(0, 0, 0);
                    lastDisplay = TimeSpan.MinValue;
                    continue;
                }

                if (connectionCount++ == 0)
                {
                    firstSample = update.Elapsed;
                }

                totalCount++;
                minimum = new PedalAxes(Math.Min(minimum.Throttle, axes.Throttle),
                    Math.Min(minimum.Brake, axes.Brake), Math.Min(minimum.Clutch, axes.Clutch));
                maximum = new PedalAxes(Math.Max(maximum.Throttle, axes.Throttle),
                    Math.Max(maximum.Brake, axes.Brake), Math.Max(maximum.Clutch, axes.Clutch));
                if (csv is not null)
                {
                    await csv.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{update.Elapsed.TotalSeconds:F6},{update.ConnectionNumber},{axes.Throttle},{axes.Brake},{axes.Clutch}")).ConfigureAwait(false);
                }

                if (lastDisplay == TimeSpan.MinValue || update.Elapsed - lastDisplay >= TimeSpan.FromMilliseconds(250))
                {
                    var elapsed = (update.Elapsed - firstSample).TotalSeconds;
                    var rate = elapsed > 0 ? (connectionCount - 1) / elapsed : 0;
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{update.Elapsed.TotalSeconds:F2}s throttle={axes.Throttle} brake={axes.Brake} clutch={axes.Clutch} delivered={rate:F1}/s min={minimum} max={maximum}"));
                    lastDisplay = update.Elapsed;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Flush the partial capture on normal duration expiry or Ctrl+C.
        }

        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Captured {totalCount} reports."));
        return totalCount;
    }
}
