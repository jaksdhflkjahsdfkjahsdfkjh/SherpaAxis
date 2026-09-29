using System.Globalization;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Cli;

internal readonly record struct ReplaySample(double ElapsedSeconds, int Connection, PedalAxes Axes)
{
    public const string Header = "elapsed_seconds,connection,throttle_device_axis,brake_device_axis,clutch_device_axis";

    public static IEnumerable<ReplaySample> Read(TextReader reader, CancellationToken cancellationToken)
    {
        if (reader.ReadLine() != Header)
        {
            throw new InvalidDataException("Replay input must have the SherpaAxis monitor CSV header.");
        }

        var previousTime = -1d;
        var previousConnection = 0;
        var lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var fields = line.Split(',');
            if (fields.Length != 5 ||
                !double.TryParse(fields[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var elapsed) ||
                !double.IsFinite(elapsed) || elapsed < 0 || elapsed < previousTime ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var connection) ||
                connection <= 0 || connection < previousConnection ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var throttle) ||
                throttle > PedalInputReport.ThrottleClutchMaximum ||
                !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var brake) ||
                brake > PedalInputReport.BrakeMaximum ||
                !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var clutch) ||
                clutch > PedalInputReport.ThrottleClutchMaximum)
            {
                throw new InvalidDataException($"Invalid replay CSV at line {lineNumber}: expected nondecreasing elapsed time and positive connection numbers, and valid device axes.");
            }

            previousTime = elapsed;
            previousConnection = connection;
            yield return new(elapsed, connection, new(throttle, brake, clutch));
        }
    }
}
