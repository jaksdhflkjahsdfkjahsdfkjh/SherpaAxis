using System.Text.Json;
using SherpaAxis.App.Services.Input;
using SherpaAxis.Bridge;
using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed partial class DashboardEngine
{
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly Dictionary<PedalAxis, AxisState> axes = [];
    private PedalConnectionState state = PedalConnectionState.Waiting;
    private string? detail = "Stopped";
    private int connection;
    private long count;
    private int historyCapacity = 600;
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
    private CalibrationCapture? calibration;
    private PedalProfile activeProfile = DefaultProfile();
    private Dictionary<PedalAxis, AxisSessionSettings> activeLimits = [];

    public DashboardEngine(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        bitePoint = new(this.clock);
        Configure(DefaultProfile(), Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, _ => new AxisSessionSettings(1000, .05)));
    }

    public static PedalProfile DefaultProfile() => new("Device-axis preview", Enum.GetValues<PedalAxis>()
        .ToDictionary(axis => axis, axis => new AxisProfile(new(0, DeviceMaximum(axis)))));

    public static double DeviceMaximum(PedalAxis axis) => axis == PedalAxis.Brake
        ? PedalInputReport.BrakeMaximum : PedalInputReport.ThrottleClutchMaximum;

    public void Configure(PedalProfile profile, IReadOnlyDictionary<PedalAxis, AxisSessionSettings> limits)
        => Configure(profile, limits, false);

    private void Configure(PedalProfile profile, IReadOnlyDictionary<PedalAxis, AxisSessionSettings> limits, bool completingCalibration)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(limits);
        lock (sync)
        {
            var processing = ProcessingConfiguration(profile);
            if (!completingCalibration && processingConfiguration is not null && processing.AsSpan().SequenceEqual(processingConfiguration)
                && SameLimits(limits) && profile.Axes.TryGetValue(PedalAxis.Clutch, out var clutch)
                && clutch.BitePoint != BiteSettings)
            {
                var oldBinding = BiteSettings?.Button;
                activeProfile = new(profile.Name, activeProfile.Axes.ToDictionary(pair => pair.Key,
                    pair => pair.Key == PedalAxis.Clutch ? pair.Value.WithBitePoint(clutch.BitePoint) : pair.Value));
                if (clutch.BitePoint?.Button is null) { bitePoint.SetEnabled(false); }
                if (clutch.BitePoint?.Button != oldBinding) { bitePoint.Release(); }
                configuration = configuration! with
                {
                    Profile = JsonSerializer.Deserialize<JsonElement>(ProfileJson.Serialize(new(activeProfile.Name,
                    activeProfile.Axes.ToDictionary(pair => pair.Key, pair => ProfileForInput(pair.Key, pair.Value)))))
                };
                configurationRevision++;
                Event("configuration-applied", configuration);
                PublishBitePointChange();
                return;
            }
            var inputProfiles = Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, axis => ProfileForInput(axis,
                profile.Axes.TryGetValue(axis, out var value) ? value : new AxisProfile(new(0, DeviceMaximum(axis)))));
            var inputLimits = limits.ToDictionary(pair => pair.Key, pair => sensorCalibration?.Map(pair.Key, pair.Value) ?? pair.Value);
            var replacements = inputProfiles.ToDictionary(pair => pair.Key, pair => pair.Value.CreateSession(inputLimits[pair.Key]));
            var snapshot = new RecordedConfiguration(JsonSerializer.Deserialize<JsonElement>(ProfileJson.Serialize(new(profile.Name, inputProfiles))),
                Array.AsReadOnly(inputLimits.Select(pair => new RecordedAxisLimits(pair.Key, pair.Value.MaximumCorrectionPerSecond,
                    pair.Value.MaximumCorrectionOutputPerSecond, pair.Value.ResetAfterSeconds)).ToArray()));
            if (!completingCalibration) { InvalidateCalibration("Processing settings changed. Start calibration again."); }
            SuspendOutput("processing settings changed");
            foreach (var (axis, session) in replacements)
            {
                var inputProfile = inputProfiles[axis];
                var replacement = new AxisState(session, new AxisProfile(inputProfile.Calibration, inputProfile.Deadzone, inputProfile.Curve).CreateProcessor());
                if (axes.TryGetValue(axis, out var old))
                {
                    replacement.Minimum = old.Minimum;
                    replacement.Maximum = old.Maximum;
                    if (sensorCalibration is not null && IsFresh(old) && Math.Abs(old.Session.BaselineCorrection) <= session.MaximumBaselineCorrection)
                    {
                        replacement.CarryCorrection = old.Session.BaselineCorrection;
                        replacement.BaselineApproved = old.BaselineApproved && old.Session.BaselineCorrection == old.Session.TargetCorrection;
                        if (replacement.BaselineApproved)
                        {
                            replacement.DriftStatus = Math.Abs(old.Session.BaselineCorrection) < .5 ? "Released baseline matches calibration; no correction needed"
                                : "Drift fixed for this session. Save corrected calibration to keep it in the controller";
                        }
                        else if (old.Session.BaselineCorrection != 0) { replacement.DriftStatus = "Drift correction paused; capture again to continue"; }
                    }
                }

                axes[axis] = replacement;
            }

            configuration = snapshot;
            activeProfile = new(profile.Name, Enum.GetValues<PedalAxis>().ToDictionary(axis => axis,
                axis => profile.Axes.TryGetValue(axis, out var value) ? value : new AxisProfile(new(0, DeviceMaximum(axis)))));
            activeLimits = new Dictionary<PedalAxis, AxisSessionSettings>(limits);
            processingConfiguration = processing;
            if (BiteSettings?.Button is null) { bitePoint.SetEnabled(false); }
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
            fixRequested = null;
            InvalidateCalibration("Input source ended. Start input and calibrate again.");
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
                axis.Automatic.Reset();
                axis.BindingReady = axis.BindingWasDown = false;
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
                var settings = activeProfile.Axes[axis];
                var released = sensorCalibration is null && settings.CorrectionMode != BaselineCorrectionMode.ByButton
                    && values.AuthorizedAt.HasValue && clock.GetElapsedTime(values.AuthorizedAt.Value) < TimeSpan.FromMilliseconds(150);
                released |= ProcessDrift(axis, values, raw, settings);
                var result = values.Session.Process(raw, elapsed, connection, released);
                if (values.CarryCorrection is { } carried)
                {
                    values.Session.RestoreBaselineCorrection(carried);
                    values.CarryCorrection = null;
                    result = values.Session.Process(raw, elapsed, connection, false);
                }
                if (values.Automatic.Learning && values.Session.BaselineCorrection == values.Session.TargetCorrection) { values.Automatic.Complete(); }
                if ((values.ManualAdjusting || values.ButtonCaptured) && values.Session.BaselineCorrection == values.Session.TargetCorrection)
                {
                    values.ManualAdjusting = false;
                    values.DriftStatus = Math.Abs(values.Session.BaselineCorrection) < .5 ? "Released baseline matches calibration; no correction needed"
                        : "Drift fixed for this session. Save corrected calibration to keep it in the controller";
                }
                if (result.ResetReason != AxisSessionResetReason.None)
                {
                    if (result.ResetReason == AxisSessionResetReason.SamplingGap) { SuspendOutput("sampling gap"); }
                    values.History.Clear();
                    values.LastPlotAt = null;
                    values.AuthorizedAt = null;
                    if (result.ResetReason == AxisSessionResetReason.SamplingGap)
                    {
                        values.ReleaseWindow.Reset(); values.BaselineApproved = false; values.ManualCapture = values.ManualAdjusting = false;
                        values.DriftStatus = "Input interrupted; drift check cancelled";
                    }
                }

                values.Current = result;
                values.LastSampleAt = clock.GetTimestamp();
                values.Minimum = Math.Min(values.Minimum, raw);
                values.Maximum = Math.Max(values.Maximum, raw);
                if (calibration is { IsActive: true } capture && capture.Axis == axis)
                {
                    calibration = capture.Include(raw);
                }
                if (!values.LastPlotAt.HasValue || elapsed - values.LastPlotAt.Value >= .02)
                {
                    values.LastPlotAt = elapsed;
                    values.History.Enqueue(new(elapsed, result.Processing.Normalized, result.Processing.FilteredNormalized, FinalOutput(axis, result.Processing.Output), raw, result.Processing.CorrectedNormalized));
                    if (values.History.Count > historyCapacity)
                    {
                        values.History.Dequeue();
                    }
                }
            }

            recording?.RecordSample(new(elapsed, connection, configurationRevision, input,
                axes[PedalAxis.Throttle].Current!.Value, axes[PedalAxis.Brake].Current!.Value, axes[PedalAxis.Clutch].Current!.Value,
                FinalOutput(PedalAxis.Clutch, axes[PedalAxis.Clutch].Current!.Value.Processing.Output)));
            output?.Submit(CurrentOutput());
        }
    }

    public void AttachOutput(IProcessedOutputSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (sync)
        {
            if (calibration is { IsActive: true })
            {
                throw new InvalidOperationException("Finish or cancel calibration before enabling virtual output.");
            }
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

    public bool CanPublishOutput
    {
        get
        {
            lock (sync)
            {
                return sourceActive && source.Kind == "live-device-axis" && calibration is not { IsActive: true }
            && count >= 2 && axes.Values.All(axis => IsFresh(axis) && clock.GetElapsedTime(axis.LastSampleAt!.Value) < TimeSpan.FromMilliseconds(100));
            }
        }
    }

    public void SetGraphWindow(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds is < .1 or > 60) { throw new ArgumentOutOfRangeException(nameof(seconds)); }
        lock (sync)
        {
            historyCapacity = Math.Max(600, (int)Math.Ceiling(seconds * 50) + 2);
            foreach (var axis in axes.Values) { while (axis.History.Count > historyCapacity) { axis.History.Dequeue(); } }
        }
    }

    public void ProcessCorrectionButtons(IButtonStateSource buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        lock (sync)
        {
            ProcessBitePointButton(buttons);
            foreach (var (axis, value) in axes)
            {
                var settings = activeProfile.Axes[axis];
                if (settings.CorrectionMode != BaselineCorrectionMode.ByButton || settings.CorrectionButton is not { } button) { continue; }
                var down = buttons.IsDown(button);
                if (!down)
                {
                    if (value.ManualCapture || value.ManualAdjusting) { continue; }
                    if (value.BindingWasDown && !value.ManualCapture && !value.ManualAdjusting)
                    {
                        value.DriftStatus = value.BaselineApproved && value.Session.BaselineCorrection == value.Session.TargetCorrection
                            ? Math.Abs(value.Session.BaselineCorrection) < .5 ? "Released baseline matches calibration; no correction needed"
                                : "Drift fixed for this session. Save corrected calibration to keep it in the controller"
                            : value.Session.BaselineCorrection == 0 ? "Drift check cancelled; no correction applied" : "Drift correction paused; capture again to continue";
                        value.ManualStarted = null;
                    }
                    value.BindingReady = true; value.BindingWasDown = false; value.AuthorizedAt = null; value.ReleaseWindow.Reset(); value.ButtonCaptured = false;
                    continue;
                }
                if (!value.BindingReady || !IsFresh(value) || sensorCalibration is null) { continue; }
                if (!value.BindingWasDown) { value.ReleaseWindow.Reset(); value.ButtonCaptured = false; if (!value.ManualCapture) { value.ManualStarted = null; } }
                value.BindingWasDown = true;
                value.AuthorizedAt = clock.GetTimestamp();
            }
        }
    }

    public void SuspendOutput(string reason)
    {
        lock (sync)
        {
            bitePoint.Release();
            if (output is not null)
            {
                output.Invalidate(reason);
                output = null;
                Event("virtual-output-disabled", new { Reason = reason });
            }
        }
    }

    private PedalOutput CurrentOutput() => new(axes[PedalAxis.Throttle].Current!.Value.Processing.Output,
        axes[PedalAxis.Brake].Current!.Value.Processing.Output, FinalOutput(PedalAxis.Clutch, axes[PedalAxis.Clutch].Current!.Value.Processing.Output));

    public CalibrationCapture StartCalibration(PedalAxis axis)
    {
        lock (sync)
        {
            var values = axes[axis];
            if (!IsFresh(values)) { throw new InvalidOperationException("Start live input or playback and wait for fresh readings, then click Calibrate."); }
            SuspendOutput("calibration started");
            RevokeRelease();
            var input = values.Current!.Value.Processing.Input;
            calibration = new(axis, connection, 0, 0, input, 0);
            Event("calibration-started", calibration);
            return calibration;
        }
    }

    public CalibrationCapture? CalibrationSnapshot()
    {
        lock (sync) { return calibration; }
    }

    public void CancelCalibration()
    {
        lock (sync)
        {
            if (calibration is not null) { Event("calibration-cancelled", calibration); }
            calibration = null;
        }
    }

    public PedalProfile CompleteCalibration(PedalAxis axis)
    {
        lock (sync)
        {
            var capture = calibration;
            if (capture is null || capture.Axis != axis) { throw new InvalidOperationException("Click Calibrate to start a new capture for this pedal."); }
            if (capture.Failure is { } failure) { throw new InvalidOperationException(failure); }
            if (!IsFresh(axes[axis])) { throw new InvalidOperationException("Input is stale. Wait for fresh readings before clicking Done, or cancel calibration."); }
            if (capture.SampleCount == 0 || capture.Maximum <= capture.Minimum) { throw new InvalidOperationException("No pedal range captured yet. Release and press the pedal before clicking Done."); }
            var old = activeProfile.Axes[axis];
            if (old.MaximumBaselineCorrection > capture.Maximum - capture.Minimum)
            {
                throw new InvalidOperationException("Captured range is smaller than the active manual-correction limit. Move through a wider range or cancel and reduce that limit.");
            }
            var calibrated = new AxisProfile(new(capture.Minimum, capture.Maximum), old.Deadzone, old.Curve,
                old.MaximumBaselineCorrection, old.Filter, old.Session, old.CorrectionMode, old.CorrectionButton, old.BitePoint);
            var replacements = activeProfile.Axes.ToDictionary(pair => pair.Key, pair => pair.Value);
            replacements[axis] = calibrated;
            var profile = new PedalProfile(activeProfile.Name, replacements);
            Configure(profile, activeLimits, true);
            calibration = null;
            Event("calibration-completed", capture);
            return profile;
        }
    }

    private void InvalidateCalibration(string reason)
    {
        if (calibration is { IsActive: true } capture)
        {
            calibration = capture with { Failure = reason };
            Event("calibration-invalidated", calibration);
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
                LastSourceTime = sourceElapsed,
                BitePoint = BitePointState
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
                double.IsFinite(pair.Value.Maximum) ? pair.Value.Maximum : null, pair.Value.History.ToArray(),
                pair.Value.Current is { } current ? FinalOutput(pair.Key, current.Processing.Output) : null)).ToArray();
            return new(state, detail, connection, count, elapsed, elapsed > firstTime ? (count - 1) / (elapsed - firstTime) : 0,
                frames, frames.Single(frame => frame.Axis == selectedAxis).History!);
        }
    }

    private bool IsFresh(AxisState axis) => state == PedalConnectionState.Connected && axis.Current.HasValue &&
        axis.LastSampleAt.HasValue && clock.GetElapsedTime(axis.LastSampleAt.Value) < TimeSpan.FromMilliseconds(250);

    private void ClearConnection()
    {
        bitePoint.Release();
        InvalidateCalibration("Connection changed. Start calibration again.");
        SuspendOutput("connection changed");
        hardware = null;
        count = 0;
        firstTime = elapsed = 0;
        foreach (var axis in axes.Values)
        {
            axis.Session.Reset();
            axis.Automatic.Reset();
            axis.BindingReady = axis.BindingWasDown = false;
            axis.ReleaseWindow.Reset(); axis.ButtonCaptured = axis.BaselineApproved = axis.ManualCapture = axis.ManualAdjusting = false;
            axis.CarryCorrection = null;
            axis.ManualStarted = axis.ManualCandidate = null;
            axis.DriftStatus = "Ready. Use Fix drift or hold the bound button to check the released pedal";
            axis.Current = null;
            axis.AuthorizedAt = null;
            axis.LastSampleAt = null;
            axis.Minimum = double.PositiveInfinity;
            axis.Maximum = double.NegativeInfinity;
            axis.History.Clear();
            axis.LastPlotAt = null;
        }
    }

    private sealed class AxisState(AxisSession session, AxisProcessor releaseGuard)
    {
        public AxisSession Session { get; } = session;
        public AxisProcessor ReleaseGuard { get; } = releaseGuard;
        public AutomaticBaseline Automatic { get; } = new();
        public StableReleaseWindow ReleaseWindow { get; } = new();
        public bool ButtonCaptured { get; set; }
        public bool BaselineApproved { get; set; }
        public bool ManualCapture { get; set; }
        public bool ManualAdjusting { get; set; }
        public double? ManualStarted { get; set; }
        public double? ManualCandidate { get; set; }
        public double? CarryCorrection { get; set; }
        public string DriftStatus { get; set; } = "Ready. Use Fix drift or hold the bound button to check the released pedal";
        public bool BindingReady { get; set; }
        public bool BindingWasDown { get; set; }
        public AxisSessionResult? Current { get; set; }
        public long? AuthorizedAt { get; set; }
        public long? LastSampleAt { get; set; }
        public double? LastPlotAt { get; set; }
        public double Minimum { get; set; } = double.PositiveInfinity;
        public double Maximum { get; set; } = double.NegativeInfinity;
        public Queue<PlotSample> History { get; } = new();
    }
}
