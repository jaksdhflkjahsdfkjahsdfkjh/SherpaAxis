using SherpaAxis.Core;

namespace SherpaAxis.App.Models;

// A release excursion arms one stable, bounded baseline capture. Steady low pressure at startup cannot arm it.
internal sealed class AutomaticBaseline
{
    private bool armed, learning;
    private double? previous, quietSince;
    private double low, high;
    private long connection;
    internal bool Learning => learning;
    internal void Reset() { armed = learning = false; previous = quietSince = null; connection = 0; }
    internal void Complete() { armed = learning = false; quietSince = null; }
    internal double? Observe(double input, double time, long currentConnection, AxisCalibration calibration, double maximum, double gap)
    {
        if (!double.IsFinite(time) || time < 0 || time < previous) { throw new ArgumentOutOfRangeException(nameof(time)); }
        if (currentConnection != connection || previous is { } last && time - last >= gap) { Reset(); }
        previous = time; connection = currentConnection;
        var band = Math.Min(maximum, calibration.Span * .005);
        if (band <= 0) { return null; }
        var distance = Math.Abs(input - calibration.ReleasedReference);
        if (distance > Math.Max(band * 4, calibration.Span * .03)) { armed = true; }
        if (!armed || distance > band)
        { quietSince = null; learning = false; return null; }
        if (quietSince is null) { quietSince = time; low = high = input; }
        low = Math.Min(low, input); high = Math.Max(high, input);
        if (high - low > calibration.Span * .0002)
        { quietSince = time; low = high = input; learning = false; }
        if (time - quietSince.Value < 1) { return null; }
        learning = true;
        return calibration.Inverted ? high : low;
    }
}
