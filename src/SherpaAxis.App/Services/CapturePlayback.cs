using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services;

public static class CapturePlayback
{
    public static async IAsyncEnumerable<PedalMonitorEvent> ReadAsync(string path, double speed = 1,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(speed) || speed <= 0 || speed > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        using var reader = new StreamReader(path);
        if (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) !=
            "elapsed_seconds,connection,throttle_device_axis,brake_device_axis,clutch_device_axis")
        {
            throw new InvalidDataException("Choose a SherpaAxis monitor CSV with the original five-column header.");
        }

        var previousTime = -1d;
        var connection = 0;
        double? origin = null;
        var started = Stopwatch.GetTimestamp();
        var lineNumber = 1;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var fields = line.Split(',');
            if (fields.Length != 5 || !double.TryParse(fields[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var elapsed) ||
                !double.IsFinite(elapsed) || elapsed < 0 || elapsed < previousTime || elapsed > TimeSpan.MaxValue.TotalSeconds - 1 ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var nextConnection) ||
                nextConnection <= 0 || nextConnection < connection ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var throttle) || throttle > PedalInputReport.ThrottleClutchMaximum ||
                !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var brake) || brake > PedalInputReport.BrakeMaximum ||
                !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var clutch) || clutch > PedalInputReport.ThrottleClutchMaximum)
            {
                throw new InvalidDataException($"Invalid capture row {lineNumber}: check timestamps, connections, and device-axis ranges.");
            }

            origin ??= elapsed;
            var due = (elapsed - origin.Value) / speed;
            while (due - Stopwatch.GetElapsedTime(started).TotalSeconds > .002)
            {
                var wait = Math.Min(.05, due - Stopwatch.GetElapsedTime(started).TotalSeconds);
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);
                }
            }

            if (nextConnection != connection)
            {
                yield return new(PedalConnectionState.Connected, TimeSpan.FromSeconds(elapsed), nextConnection);
            }

            yield return new(PedalConnectionState.Connected, TimeSpan.FromSeconds(elapsed), nextConnection, new(throttle, brake, clutch));
            previousTime = elapsed;
            connection = nextConnection;
        }

        if (previousTime < 0)
        {
            throw new InvalidDataException("The capture has no samples.");
        }
    }
}
