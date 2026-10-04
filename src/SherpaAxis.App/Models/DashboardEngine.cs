using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed class DashboardEngine
{
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly Dictionary<PedalAxis, AxisState> axes = [];
    private PedalConnectionState state = PedalConnectionState.Waiting;
    private string? detail = "Stopped";
    private int connection;
    private long count;
    private double firstTime;
    private double elapsed;

    public DashboardEngine(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        Configure(DefaultProfile(), Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, _ => new AxisSessionSettings(1000, .05)));
    }

    public static PedalProfile DefaultProfile() => new("Device-axis preview", Enum.GetValues<PedalAxis>()
        .ToDictionary(axis => axis, axis => new AxisProfile(new(0, DeviceMaximum(axis)))));

    public static double DeviceMaximum(PedalAxis axis) => axis == PedalAxis.Brake
        ? PedalInputReport.BrakeMaximum : PedalInputReport.ThrottleClutchMaximum;

    public void Configure(PedalProfile profile, IReadOnlyDictionary<PedalAxis, AxisSessionSettings> limits)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(limits);
        var replacements = Enum.GetValues<PedalAxis>().ToDictionary(axis => axis,
            axis => (profile.Axes.TryGetValue(axis, out var value) ? value : new AxisProfile(new(0, DeviceMaximum(axis))))
                .CreateSession(limits[axis]));
        lock (sync)
        {
            foreach (var (axis, session) in replacements)
            {
                var replacement = new AxisState(session);
                if (axes.TryGetValue(axis, out var old))
                {
                    replacement.Minimum = old.Minimum;
                    replacement.Maximum = old.Maximum;
                }

                axes[axis] = replacement;
            }
        }
    }

    public void Begin()
    {
        lock (sync)
        {
            ClearConnection();
            connection = 0;
            state = PedalConnectionState.Waiting;
            detail = "Waiting for samples";
        }
    }

    public void End(string message)
    {
        lock (sync)
        {
            state = PedalConnectionState.Waiting;
            detail = message;
            foreach (var axis in axes.Values)
            {
                axis.Session.Reset();
                axis.AuthorizedAt = null;
            }
        }
    }

    public void Accept(PedalMonitorEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (sync)
        {
            state = item.State;
            detail = item.Detail;
            if (item.State != PedalConnectionState.Connected || item.ConnectionNumber != connection)
            {
                ClearConnection();
                connection = item.ConnectionNumber;
            }

            if (item.Axes is not { } input)
            {
                return;
            }

            elapsed = item.Elapsed.TotalSeconds;
            if (count == 0)
            {
                firstTime = elapsed;
            }

            count++;
            foreach (var (axis, values) in axes)
            {
                var raw = axis switch { PedalAxis.Throttle => input.Throttle, PedalAxis.Brake => input.Brake, _ => input.Clutch };
                var released = values.AuthorizedAt.HasValue && clock.GetElapsedTime(values.AuthorizedAt.Value) < TimeSpan.FromMilliseconds(150);
                var result = values.Session.Process(raw, elapsed, connection, released);
                if (result.ResetReason != AxisSessionResetReason.None)
                {
                    values.History.Clear();
                    values.LastPlotAt = null;
                    values.AuthorizedAt = null;
                }

                values.Current = result;
                values.LastSampleAt = clock.GetTimestamp();
                values.Minimum = Math.Min(values.Minimum, raw);
                values.Maximum = Math.Max(values.Maximum, raw);
                if (!values.LastPlotAt.HasValue || elapsed - values.LastPlotAt.Value >= .02)
                {
                    values.LastPlotAt = elapsed;
                    values.History.Enqueue(new(elapsed, result.Processing.Normalized, result.Processing.FilteredNormalized, result.Processing.Output, raw));
                    if (values.History.Count > 600)
                    {
                        values.History.Dequeue();
                    }
                }
            }
        }
    }

    public void ConfirmBaseline(PedalAxis axis)
    {
        lock (sync)
        {
            var values = axes[axis];
            if (!IsFresh(values))
            {
                throw new InvalidOperationException("A fresh sample is required to confirm the released baseline.");
            }

            values.Session.ConfirmReleasedBaseline(values.Current!.Value.Processing.Input);
        }
    }

    public void DisableCompensation(PedalAxis axis)
    {
        lock (sync)
        {
            axes[axis].Session.DisableCompensation();
        }
    }

    public void AuthorizeRelease(PedalAxis axis)
    {
        lock (sync)
        {
            if (IsFresh(axes[axis]))
            {
                axes[axis].AuthorizedAt = clock.GetTimestamp();
            }
        }
    }

    public void RevokeRelease()
    {
        lock (sync)
        {
            foreach (var values in axes.Values)
            {
                values.AuthorizedAt = null;
            }
        }
    }

    public DashboardFrame Snapshot(PedalAxis selectedAxis)
    {
        lock (sync)
        {
            var frames = axes.Select(pair => new AxisFrame(pair.Key, pair.Value.Current, IsFresh(pair.Value),
                double.IsFinite(pair.Value.Minimum) ? pair.Value.Minimum : null,
                double.IsFinite(pair.Value.Maximum) ? pair.Value.Maximum : null)).ToArray();
            return new(state, detail, connection, count, elapsed, elapsed > firstTime ? (count - 1) / (elapsed - firstTime) : 0,
                frames, axes[selectedAxis].History.ToArray());
        }
    }

    private bool IsFresh(AxisState axis) => state == PedalConnectionState.Connected && axis.Current.HasValue &&
        axis.LastSampleAt.HasValue && clock.GetElapsedTime(axis.LastSampleAt.Value) < TimeSpan.FromMilliseconds(250);

    private void ClearConnection()
    {
        count = 0;
        firstTime = elapsed = 0;
        foreach (var axis in axes.Values)
        {
            axis.Session.Reset();
            axis.Current = null;
            axis.AuthorizedAt = null;
            axis.LastSampleAt = null;
            axis.Minimum = double.PositiveInfinity;
            axis.Maximum = double.NegativeInfinity;
            axis.History.Clear();
            axis.LastPlotAt = null;
        }
    }

    private sealed class AxisState(AxisSession session)
    {
        public AxisSession Session { get; } = session;
        public AxisSessionResult? Current { get; set; }
        public long? AuthorizedAt { get; set; }
        public long? LastSampleAt { get; set; }
        public double? LastPlotAt { get; set; }
        public double Minimum { get; set; } = double.PositiveInfinity;
        public double Maximum { get; set; } = double.NegativeInfinity;
        public Queue<PlotSample> History { get; } = new();
    }
}
