using System.Text.Json;
using SherpaAxis.Bridge;
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
    private IDashboardRecording? recording;
    private RecordedConfiguration? configuration;
    private long configurationRevision;
    private RecordingSource source = new("unspecified");
    private RecordedHardware? hardware;
    private bool sourceActive;
    private double sourceElapsed;
    private IProcessedOutputSink? output;

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
        var snapshot = new RecordedConfiguration(JsonSerializer.Deserialize<JsonElement>(ProfileJson.Serialize(profile)),
            Array.AsReadOnly(limits.Select(pair => new RecordedAxisLimits(pair.Key, pair.Value.MaximumCorrectionPerSecond,
                pair.Value.MaximumCorrectionOutputPerSecond, pair.Value.ResetAfterSeconds)).ToArray()));
        lock (sync)
        {
            SuspendOutput("processing settings changed");
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

            configuration = snapshot;
            configurationRevision++;
            Event("configuration-applied", configuration);
        }
    }

    public void Begin(RecordingSource? sourceContext = null)
    {
        lock (sync)
        {
            Event("source-restarted");
            recording?.Complete("source-restarted");
            recording = null;
            sourceActive = true;
            sourceElapsed = 0;
            ClearConnection();
            source = sourceContext ?? new("unspecified");
            hardware = null;
            connection = 0;
            state = PedalConnectionState.Waiting;
            detail = "Waiting for samples";
            Event("source-started", source);
        }
    }

    public void End(string message)
    {
        lock (sync)
        {
            SuspendOutput("input source ended");
            Event("source-ended", new { Message = message });
            recording?.Complete(message.StartsWith("Source error:", StringComparison.Ordinal) ? "source-error" : message);
            recording = null;
            sourceActive = false;
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
            sourceElapsed = item.Elapsed.TotalSeconds;
            state = item.State;
            detail = item.Detail;
            if (item.Axes is null)
            {
                recording?.RecordEvent(new("connection-state", item.Elapsed.TotalSeconds, configurationRevision,
                    new { item.State, item.ConnectionNumber, item.Detail }));
            }
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
                    if (result.ResetReason == AxisSessionResetReason.SamplingGap) { SuspendOutput("sampling gap"); }
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
                    values.History.Enqueue(new(elapsed, result.Processing.Normalized, result.Processing.FilteredNormalized, result.Processing.Output, raw, result.Processing.CorrectedNormalized));
                    if (values.History.Count > 600)
                    {
                        values.History.Dequeue();
                    }
                }
            }

            recording?.RecordSample(new(elapsed, connection, configurationRevision, input,
                axes[PedalAxis.Throttle].Current!.Value, axes[PedalAxis.Brake].Current!.Value, axes[PedalAxis.Clutch].Current!.Value));
            output?.Submit(CurrentOutput());
        }
    }

    public void AttachOutput(IProcessedOutputSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (sync)
        {
            if (!sourceActive || source.Kind != "live-device-axis" || axes.Values.Any(axis => !IsFresh(axis) || clock.GetElapsedTime(axis.LastSampleAt!.Value) >= TimeSpan.FromMilliseconds(100)))
            {
                throw new InvalidOperationException("Virtual output requires fresh live pedal input. Replay cannot feed a controller.");
            }
            SuspendOutput("output replaced");
            output = sink;
            output.Submit(CurrentOutput());
            Event("virtual-output-enabled", new { Mapping = "throttle:X,brake:Y,clutch:Z" });
        }
    }

    public void SuspendOutput(string reason)
    {
        lock (sync)
        {
            if (output is not null)
            {
                output.Invalidate(reason);
                output = null;
                Event("virtual-output-disabled", new { Reason = reason });
            }
        }
    }

    private PedalOutput CurrentOutput() => new(axes[PedalAxis.Throttle].Current!.Value.Processing.Output,
        axes[PedalAxis.Brake].Current!.Value.Processing.Output, axes[PedalAxis.Clutch].Current!.Value.Processing.Output);

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
            Event("baseline-requested", new { Axis = axis, ReleasedInput = values.Current.Value.Processing.Input, values.Session.TargetCorrection });
        }
    }

    public void DisableCompensation(PedalAxis axis)
    {
        lock (sync)
        {
            axes[axis].Session.DisableCompensation();
            Event("compensation-disable-requested", new { Axis = axis });
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
            var wasAuthorized = axes.Values.Any(axis => axis.AuthorizedAt.HasValue);
            foreach (var values in axes.Values)
            {
                values.AuthorizedAt = null;
            }

            if (wasAuthorized)
            {
                Event("release-control-revoked");
            }
        }
    }

    public void AttachRecording(IDashboardRecording target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (sync)
        {
            if (recording is not null)
            {
                throw new InvalidOperationException("Finish the current recording before starting another.");
            }

            if (!sourceActive)
            {
                throw new InvalidOperationException("Start live input or playback before recording.");
            }

            recording = target;
            Event("recording-started", new
            {
                Source = source,
                InputUnits = "firmware-device-axis-counts",
                HardwareMode = "notContinuouslyVerified",
                Configuration = configuration,
                HardwareSnapshot = hardware,
                Connection = connection,
                RuntimeSeed = axes.ToDictionary(pair => pair.Key, pair => new
                {
                    LastResult = pair.Value.Current,
                    pair.Value.Session.BaselineCorrection,
                    pair.Value.Session.TargetCorrection,
                    LastSampleTime = pair.Value.Current.HasValue ? (double?)elapsed : null,
                    ReleasePermissionRemainingMilliseconds = pair.Value.AuthorizedAt.HasValue
                        ? Math.Max(0, 150 - clock.GetElapsedTime(pair.Value.AuthorizedAt.Value).TotalMilliseconds) : 0
                }),
                LastSourceTime = sourceElapsed
            });
        }
    }

    public void DetachRecording(string reason = "user-finished")
    {
        lock (sync)
        {
            Event("recording-stop-requested", new { Reason = reason });
            recording?.Complete(reason);
            recording = null;
        }
    }

    public void RecordHardware(CalibrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (sync)
        {
            hardware = new(snapshot.CapturedAtUtc, snapshot.Calibration, connection);
            Event("hardware-calibration-read", hardware);
        }
    }

    private void Event(string kind, object? details = null) => recording?.RecordEvent(new(kind, sourceElapsed, configurationRevision, details));

    public DashboardFrame Snapshot(PedalAxis selectedAxis)
    {
        lock (sync)
        {
            var frames = axes.Select(pair => new AxisFrame(pair.Key, pair.Value.Current, IsFresh(pair.Value),
                double.IsFinite(pair.Value.Minimum) ? pair.Value.Minimum : null,
                double.IsFinite(pair.Value.Maximum) ? pair.Value.Maximum : null, pair.Value.History.ToArray())).ToArray();
            return new(state, detail, connection, count, elapsed, elapsed > firstTime ? (count - 1) / (elapsed - firstTime) : 0,
                frames, frames.Single(frame => frame.Axis == selectedAxis).History!);
        }
    }

    private bool IsFresh(AxisState axis) => state == PedalConnectionState.Connected && axis.Current.HasValue &&
        axis.LastSampleAt.HasValue && clock.GetElapsedTime(axis.LastSampleAt.Value) < TimeSpan.FromMilliseconds(250);

    private void ClearConnection()
    {
        SuspendOutput("connection changed");
        hardware = null;
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
