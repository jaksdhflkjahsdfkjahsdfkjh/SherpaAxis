using SherpaAxis.Core;

namespace SherpaAxis.App.Models;

internal sealed class AutomaticBaseline
{
    internal const int RequiredCycles = 5;
    private readonly StableReleaseWindow window = new();
    private readonly List<double> releases = [];
    private double? previous, batchStarted, peak, lastRelease, approved;
    private long connection;
    private double maximumJitter;
    internal double NoiseTolerance { get; private set; }
    internal bool Learning { get; private set; }
    internal int CycleCount => releases.Count;
    internal string Status { get; private set; } = "Waiting for five consistent press/release cycles";

    internal void Reset()
    {
        previous = batchStarted = peak = lastRelease = approved = null;
        releases.Clear(); window.Reset(); Learning = false; connection = 0;
        maximumJitter = NoiseTolerance = 0;
        Status = "Waiting for five consistent press/release cycles";
    }

    internal void Complete()
    {
        approved = peak = lastRelease = batchStarted = null;
        releases.Clear(); window.Reset(); Learning = false;
        maximumJitter = NoiseTolerance = 0;
        Status = "Drift correction accepted after five consistent cycles";
    }

    internal void Reject(string reason) { Complete(); Status = reason; }
    internal void Suspend(string reason) { Learning = false; Status = reason; }

    internal double? Observe(double input, double time, long currentConnection, AxisCalibration calibration, double maximum, double gap)
    {
        if (!double.IsFinite(input) || !double.IsFinite(time) || time < 0 || time < previous || currentConnection <= 0)
        { throw new ArgumentOutOfRangeException(nameof(time)); }
        if (currentConnection != connection || previous is { } last && time - last >= gap) { Reset(); }
        previous = time; connection = currentConnection;
        var span = calibration.Span;
        if (maximum <= 0) { return null; }
        if (batchStarted is { } start && time - start > 120)
        { Complete(); Status = "Cycle checks expired; no adjustment"; }
        peak = Math.Max(peak ?? input, input);
        if (approved is { } prior && input - prior >= span * .1)
        {
            approved = null; releases.Clear(); batchStarted = null;
            maximumJitter = NoiseTolerance = 0;
            peak = input; window.Reset(); Learning = false;
        }
        if (lastRelease is { } released)
        {
            if (input - released < span * .1) { return null; }
            lastRelease = null; peak = input; approved = null; Learning = false; window.Reset();
        }
        if (peak.Value - input < span * .1)
        { window.Reset(); Learning = false; return null; }
        var candidate = window.Observe(input, time, span);
        if (candidate is null) { Learning = false; return null; }
        if (approved is { } accepted)
        {
            if (Math.Abs(input - accepted) <= NoiseTolerance)
            { Learning = true; return accepted; }
            approved = null; releases.Clear(); batchStarted = null;
            maximumJitter = NoiseTolerance = 0;
            lastRelease = peak = input; window.Reset();
            Status = "Release values disagree; no adjustment";
            return null;
        }
        var tolerance = Math.Clamp(Math.Max(maximumJitter, window.Variation) * 2, span * .0005, span * .002);
        if (releases.Count > 0 && Math.Max(releases.Max(), candidate.Value) - Math.Min(releases.Min(), candidate.Value) > tolerance)
        {
            releases.Clear(); batchStarted = null; lastRelease = candidate; window.Reset();
            maximumJitter = NoiseTolerance = 0;
            Status = "Release values disagree; no adjustment";
            return null;
        }
        batchStarted ??= time;
        maximumJitter = Math.Max(maximumJitter, window.Variation);
        NoiseTolerance = tolerance;
        releases.Add(candidate.Value);
        Status = $"Consistent release cycles: {releases.Count}/5";
        if (releases.Count < RequiredCycles) { lastRelease = candidate; window.Reset(); return null; }
        var estimatedDrift = releases.Max() - calibration.Minimum;
        var limit = Math.Min(maximum, span * .25);
        if (Math.Abs(estimatedDrift) > limit)
        {
            Reject(FormattableString.Invariant($"Five cycles agree, but estimated drift {estimatedDrift:+0;-0;0} counts exceeds the {limit:0}-count automatic limit; manual verification is required"));
            return null;
        }
        approved = releases.Max(); Learning = true;
        Status = "Five cycles agree; correcting only during stable release";
        return Math.Abs(input - approved.Value) <= NoiseTolerance ? approved : null;
    }
}
