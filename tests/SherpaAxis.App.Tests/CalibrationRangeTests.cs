using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Bridge;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class CalibrationRangeTests
{
    [Theory]
    [InlineData(PedalAxis.Throttle)]
    [InlineData(PedalAxis.Brake)]
    [InlineData(PedalAxis.Clutch)]
    public void CapturesEveryUnfilteredReportInTheCaptureWindowAndAppliesOnlyThatPedal(PedalAxis axis)
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine) { SelectedAxis = axis };
        model.Editor.Minimum = "200";
        model.Editor.Maximum = "400";
        model.Editor.Inverted = true;
        model.SetCurvePreset(axis, "Progressive");
        model.SetFilterPreset("Smooth");
        model.Apply();
        engine.Accept(Sample(axis, 0, 0));
        engine.Accept(Sample(axis, .002, (int)DashboardEngine.DeviceMaximum(axis)));
        model.AdvanceCalibration();
        model.GraphPaused = true;
        engine.Accept(Sample(axis, .006, 100));
        engine.Accept(Sample(axis, .008, 500));
        engine.Accept(Sample(axis, .010, 300));
        // Neither extremum is present in the latest UI sample or decimated graph.
        Assert.Equal(100, engine.CalibrationSnapshot()!.Minimum);
        Assert.Equal(500, engine.CalibrationSnapshot()!.Maximum);
        Assert.Equal(3, engine.CalibrationSnapshot()!.SampleCount);
        model.AdvanceCalibration();
        Assert.False(model.IsCalibrating);
        Assert.Equal("100", model.Editor.Minimum);
        Assert.Equal("500", model.Editor.Maximum);
        Assert.False(model.Editor.Inverted);
        Assert.Equal("Smooth", model.FilterPreset);
        Assert.Equal("Progressive", model.Editor.CurveKind);
        var profile = model.ValidateEditors().Profile;
        Assert.All(profile.Axes.Where(p => p.Key != axis), pair =>
        {
            Assert.Equal(0, pair.Value.Calibration.Minimum);
            Assert.Equal(DashboardEngine.DeviceMaximum(pair.Key), pair.Value.Calibration.Maximum);
        });
        foreach (var (input, normalized) in new[] { (0, 0d), (100, 0d), (300, .5), (500, 1d), (600, 1d) })
        {
            // Gaps reseed the retained filter so each static endpoint can be checked exactly.
            engine.Accept(Sample(axis, 1 + input * .2, input));
            var result = engine.Snapshot(axis).Axes.Single(a => a.Axis == axis).Result!.Value.Processing;
            Assert.Equal(normalized, result.Normalized);
            Assert.Equal(normalized * normalized, result.Output, 12);
        }
        var restored = ProfileJson.Deserialize(ProfileJson.Serialize(profile));
        Assert.Equal(100, restored.Axes[axis].Calibration.Minimum);
        Assert.Equal(500, restored.Axes[axis].Calibration.Maximum);
        Assert.False(restored.Axes[axis].Calibration.Inverted);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("pedal")]
    [InlineData("profile")]
    [InlineData("source")]
    [InlineData("disconnect")]
    [InlineData("reconnect")]
    [InlineData("configure")]
    public void InterruptedCapturesCannotChangeCalibrationOrMergeConnections(string reason)
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        engine.Accept(Sample(PedalAxis.Brake, 0, 100));
        model.AdvanceCalibration();
        engine.Accept(Sample(PedalAxis.Brake, .001, 100));
        engine.Accept(Sample(PedalAxis.Brake, .002, 500));
        switch (reason)
        {
            case "cancel": model.CancelCalibration(); break;
            case "pedal": model.SelectedAxis = PedalAxis.Clutch; break;
            case "profile": model.Load(DashboardEngine.DefaultProfile()); break;
            case "source": engine.End("Stopped"); break;
            case "disconnect": engine.Accept(new(PedalConnectionState.Disconnected, TimeSpan.FromSeconds(.004), 1)); break;
            case "reconnect": engine.Accept(Sample(PedalAxis.Brake, .004, 1000) with { ConnectionNumber = 2 }); break;
            default: model.Apply(); break;
        }
        Assert.Throws<InvalidOperationException>(() => engine.CompleteCalibration(PedalAxis.Brake));
        model.Refresh();
        Assert.False(model.IsCalibrating);
        Assert.Equal(0, model.ValidateEditors().Profile.Axes[PedalAxis.Brake].Calibration.Minimum);
        Assert.Equal(DashboardEngine.DeviceMaximum(PedalAxis.Brake), model.ValidateEditors().Profile.Axes[PedalAxis.Brake].Calibration.Maximum);
    }

    [Fact]
    public void StaleAndMotionlessInputCannotFinishAndRetryStartsWithFreshBounds()
    {
        var clock = new ManualClock();
        var engine = new DashboardEngine(clock);
        var model = new DashboardViewModel(engine);
        engine.Accept(Sample(PedalAxis.Brake, 0, 100));
        clock.Advance(251);
        Assert.Throws<InvalidOperationException>(model.AdvanceCalibration);
        Assert.False(model.IsCalibrating);
        engine.Accept(Sample(PedalAxis.Brake, .002, 100));
        model.AdvanceCalibration();
        Assert.Throws<InvalidOperationException>(model.AdvanceCalibration);
        Assert.Contains("No pedal range", model.CalibrationInfo, StringComparison.Ordinal);
        engine.Accept(Sample(PedalAxis.Brake, .003, 100));
        engine.Accept(Sample(PedalAxis.Brake, .004, 500));
        clock.Advance(251);
        Assert.Throws<InvalidOperationException>(model.AdvanceCalibration);
        Assert.True(model.IsCalibrating);
        Assert.Equal("0", model.Editor.Minimum);
        engine.Accept(Sample(PedalAxis.Brake, .006, 300));
        model.AdvanceCalibration();
        engine.Accept(Sample(PedalAxis.Brake, .008, 250));
        model.AdvanceCalibration();
        engine.Accept(Sample(PedalAxis.Brake, .010, 250));
        Assert.Equal(250, engine.CalibrationSnapshot()!.Minimum);
        Assert.Equal(250, engine.CalibrationSnapshot()!.Maximum);
        model.CancelCalibration();
        Assert.Equal("100", model.Editor.Minimum);
        Assert.Equal("500", model.Editor.Maximum);
    }

    [Fact]
    public void CompletingCalibrationPreservesUnappliedDraftsWithoutActivatingThem()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine) { SelectedAxis = PedalAxis.Throttle };
        model.Editor.Minimum = "invalid";
        model.SelectedAxis = PedalAxis.Brake;
        model.SetCurvePreset(PedalAxis.Brake, "Progressive");
        engine.Accept(Sample(PedalAxis.Brake, 0, 100));
        model.AdvanceCalibration();
        engine.Accept(Sample(PedalAxis.Brake, .001, 100));
        engine.Accept(Sample(PedalAxis.Brake, .002, 500));
        model.AdvanceCalibration();
        engine.Accept(Sample(PedalAxis.Brake, .004, 300));
        Assert.Equal(.5, engine.Snapshot(PedalAxis.Brake).Axes[1].Result!.Value.Processing.Output);
        Assert.Equal("Progressive", model.Editor.CurveKind);
        model.SelectedAxis = PedalAxis.Throttle;
        Assert.Equal("invalid", model.Editor.Minimum);
    }

    [Fact]
    public void CalibrationDisarmsOutputAndCannotBeReenabledUntilFinishedOrCancelled()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        engine.Begin(new("live-device-axis"));
        engine.Accept(Sample(PedalAxis.Brake, 0, 100));
        var sink = new OutputSink();
        engine.AttachOutput(sink);
        model.AdvanceCalibration();
        Assert.Equal("calibration started", sink.Invalidated);
        Assert.Throws<InvalidOperationException>(() => engine.AttachOutput(new OutputSink()));
        engine.Accept(Sample(PedalAxis.Brake, .001, 100));
        engine.Accept(Sample(PedalAxis.Brake, .002, 500));
        model.AdvanceCalibration();
        var count = sink.Submissions;
        engine.Accept(Sample(PedalAxis.Brake, .004, 300));
        Assert.Equal(count, sink.Submissions);
    }

    [Fact]
    public void IncompatibleManualCorrectionLimitLeavesSettingsUnchanged()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        model.Editor.MaximumCorrection = "1000";
        model.Apply();
        engine.Accept(Sample(PedalAxis.Brake, 0, 100));
        model.AdvanceCalibration();
        engine.Accept(Sample(PedalAxis.Brake, .001, 100));
        engine.Accept(Sample(PedalAxis.Brake, .002, 500));
        Assert.Throws<InvalidOperationException>(model.AdvanceCalibration);
        Assert.True(model.IsCalibrating);
        Assert.Contains("manual-correction limit", model.CalibrationInfo, StringComparison.Ordinal);
        Assert.Equal("0", model.Editor.Minimum);
        Assert.Equal("1000", model.Editor.MaximumCorrection);
    }

    [Fact]
    public void RecordingOrdersCalibrationAndConfigurationBeforeReportsUsingTheNewBounds()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        engine.Begin(new("live-device-axis"));
        engine.Accept(Sample(PedalAxis.Brake, 0, 100));
        var recording = new RecordingProbe();
        engine.AttachRecording(recording);
        model.AdvanceCalibration();
        engine.Accept(Sample(PedalAxis.Brake, .002, 100));
        engine.Accept(Sample(PedalAxis.Brake, .004, 500));
        model.AdvanceCalibration();
        engine.Accept(Sample(PedalAxis.Brake, .006, 300));
        Assert.Collection(recording.Events,
            e => Assert.Equal("recording-started", e.Kind),
            e => Assert.Equal("calibration-started", e.Kind),
            e => Assert.Equal("configuration-applied", e.Kind),
            e => Assert.Equal("calibration-completed", e.Kind));
        var capture = Assert.IsType<CalibrationCapture>(recording.Events[^1].Details);
        Assert.Equal(2, capture.SampleCount);
        Assert.Equal(100, capture.Minimum);
        Assert.Equal(500, capture.Maximum);
        Assert.Equal(3, recording.Reports.Count);
        Assert.True(recording.Reports[^1].ConfigurationRevision > recording.Reports[0].ConfigurationRevision);
        Assert.Equal(recording.Events[^1].ConfigurationRevision, recording.Reports[^1].ConfigurationRevision);
        Assert.Equal(.5, recording.Reports[^1].Brake.Processing.Output);
    }

    private static PedalMonitorEvent Sample(PedalAxis axis, double time, int value) => new(PedalConnectionState.Connected,
        TimeSpan.FromSeconds(time), 1, new(axis == PedalAxis.Throttle ? value : 0, axis == PedalAxis.Brake ? value : 0, axis == PedalAxis.Clutch ? value : 0));

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public void Advance(long milliseconds) => timestamp += milliseconds;
    }

    private sealed class OutputSink : IProcessedOutputSink
    {
        public string? Invalidated { get; private set; }
        public int Submissions { get; private set; }
        public void Submit(PedalOutput output) => Submissions++;
        public void Invalidate(string reason) => Invalidated = reason;
    }

    private sealed class RecordingProbe : IDashboardRecording
    {
        public List<RecordedEvent> Events { get; } = [];
        public List<RecordedSample> Reports { get; } = [];
        public void RecordSample(RecordedSample sample) => Reports.Add(sample);
        public void RecordEvent(RecordedEvent item) => Events.Add(item);
        public void Complete(string reason) { }
    }
}
