using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Input;
using SherpaAxis.App.Services.Recording;
using SherpaAxis.Bridge;
using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class ClutchBitePointTests
{
    [Fact]
    public void DoubleTapTogglesOnSecondPressAndThatPressCanBecomeAHold()
    {
        var clock = new ManualClock();
        var control = new ClutchBitePointControl(clock);
        control.Update(true);
        clock.Ticks = 50; control.Update(false);
        Assert.False(control.Enabled); Assert.False(control.Holding);
        clock.Ticks = 120; control.Update(true);
        Assert.True(control.Enabled); Assert.False(control.Holding);
        clock.Ticks = 369; control.Update(true);
        Assert.False(control.Holding);
        clock.Ticks = 370; control.Update(true);
        Assert.True(control.Holding);
        clock.Ticks = 400; control.Update(false);
        Assert.False(control.Holding); Assert.True(control.Enabled);
        clock.Ticks = 450; control.Update(true);
        Assert.True(control.Enabled);
        clock.Ticks = 480; control.Update(false);
        clock.Ticks = 520; control.Update(true);
        Assert.False(control.Enabled); Assert.False(control.Holding);
    }

    [Theory]
    [InlineData(20, 371)]
    [InlineData(250, 300)]
    [InlineData(800, 900)]
    public void ExpiredTapsAndLongHoldsDoNotToggle(long released, long nextPress)
    {
        var clock = new ManualClock();
        var control = new ClutchBitePointControl(clock);
        control.Update(true);
        clock.Ticks = released; control.Update(false);
        clock.Ticks = nextPress; control.Update(true);
        Assert.False(control.Enabled);
    }

    [Fact]
    public void QuickTapsNeverApplyOutputAndRepeatedDownDoesNotToggle()
    {
        var clock = new ManualClock();
        var control = new ClutchBitePointControl(clock);
        control.SetEnabled(true); control.Update(true);
        for (var i = 1; i < 250; i++) { clock.Ticks = i; control.Update(true); Assert.True(control.Enabled); Assert.False(control.Holding); }
        control.Update(false);
        Assert.False(control.Holding);
    }

    [Fact]
    public void EnablingAnExistingHoldAndReconnectingDoNotRequireAnotherPress()
    {
        var clock = new ManualClock();
        var control = new ClutchBitePointControl(clock);
        control.Update(true);
        clock.Ticks = 300; control.Update(true);
        control.SetEnabled(true);
        Assert.True(control.Holding);
        control.Release();
        Assert.True(control.Enabled); Assert.False(control.Holding);
        control.Update(true);
        clock.Ticks = 550; control.Update(true);
        Assert.True(control.Holding);
    }

    [Fact]
    public void RenamingAProfileKeepsLiveOutputFilterHistoryAndHeldBitePoint()
    {
        var rig = new Rig(new(new(0, 16384), filter: new(.1)));
        rig.Engine.SetBitePointEnabled(true);
        rig.Buttons.Set(Rig.Button, true); rig.Poll(); rig.Advance(250);
        rig.Clock.Ticks += 10; rig.Sample(8192);
        var before = rig.Engine.Snapshot(PedalAxis.Clutch);
        rig.Configure(.4, name: "Renamed launch");
        var after = rig.Engine.Snapshot(PedalAxis.Clutch);
        Assert.Equal(0, rig.Sink.Invalidations);
        Assert.True(rig.Engine.BitePointState.Holding);
        Assert.Equal(before.SampleCount, after.SampleCount);
        Assert.Equal(before.Axes.Single(a => a.Axis == PedalAxis.Clutch).Result, after.Axes.Single(a => a.Axis == PedalAxis.Clutch).Result);
        rig.Clock.Ticks += 10; rig.Sample(8192);
        Assert.False(rig.Engine.Snapshot(PedalAxis.Clutch).Axes.Single(a => a.Axis == PedalAxis.Clutch).Result!.Value.Processing.FilterReset);
    }

    [Fact]
    public void OnlyFinalClutchOutputChangesAndLiveEditsKeepTheOutputSinkAndFilterState()
    {
        var rig = new Rig();
        rig.Engine.SetBitePointEnabled(true);
        rig.Buttons.Set(Rig.Button, true); rig.Poll(); rig.Advance(250);
        Assert.True(rig.Engine.BitePointState.Holding);
        Assert.Equal(new PedalOutput(.25, .5, .4), rig.Sink.Last);
        var frame = rig.Engine.Snapshot(PedalAxis.Clutch);
        var clutch = frame.Axes.Single(axis => axis.Axis == PedalAxis.Clutch);
        Assert.Equal(0, clutch.Result!.Value.Processing.Output);
        Assert.Equal(.4, clutch.FinalOutput);
        Assert.Equal(.4, frame.History[^1].Output);
        var count = frame.SampleCount;
        rig.Configure(.6);
        Assert.Equal(.6, rig.Sink.Last.Clutch);
        Assert.Equal(count, rig.Engine.Snapshot(PedalAxis.Clutch).SampleCount);
        Assert.NotNull(rig.Engine.Snapshot(PedalAxis.Clutch).Axes.Single(axis => axis.Axis == PedalAxis.Clutch).Result);
        Assert.True(rig.Engine.BitePointState.Holding);
        Assert.Equal(0, rig.Sink.Invalidations);
        rig.Sample(12288);
        Assert.Equal(.75, rig.Sink.Last.Clutch);
        rig.Buttons.Set(Rig.Button, false); rig.Poll();
        Assert.Equal(.75, rig.Sink.Last.Clutch);
        rig.Sample(0);
        Assert.Equal(0, rig.Sink.Last.Clutch);
        Assert.Equal(0, rig.Sink.Invalidations);
    }

    [Fact]
    public void ToggleOffAndRemovingBindingImmediatelyReleaseOutput()
    {
        var rig = new Rig();
        rig.Tap(); rig.Advance(50); rig.Buttons.Set(Rig.Button, true); rig.Poll();
        Assert.True(rig.Engine.BitePointState.Enabled);
        rig.Advance(250);
        Assert.Equal(.4, rig.Sink.Last.Clutch);
        rig.Engine.SetBitePointEnabled(false);
        Assert.Equal(0, rig.Sink.Last.Clutch);
        rig.Engine.SetBitePointEnabled(true);
        Assert.Equal(.4, rig.Sink.Last.Clutch);
        rig.Configure(.4, bind: false);
        Assert.Equal(0, rig.Sink.Last.Clutch);
        Assert.False(rig.Engine.BitePointState.Enabled);
        Assert.Equal(0, rig.Sink.Invalidations);
        rig.Engine.SetBitePointEnabled(true);
        Assert.False(rig.Engine.BitePointState.Enabled);
    }

    [Fact]
    public void StaleInputAndReconnectClearTheHoldButRetainEnabledState()
    {
        var rig = new Rig();
        rig.Engine.SetBitePointEnabled(true);
        rig.Buttons.Set(Rig.Button, true); rig.Poll(); rig.Advance(250);
        rig.Clock.Ticks += 251; rig.Poll();
        Assert.False(rig.Engine.BitePointState.Holding);
        Assert.False(rig.Engine.CanPublishOutput);
        rig.Engine.Begin(new("live-device-axis"));
        rig.Sample(0); rig.Sample(0); rig.Poll(); rig.Advance(250);
        Assert.True(rig.Engine.BitePointState.Holding);
        Assert.True(rig.Engine.BitePointState.Enabled);
        rig.Engine.End("Stopped");
        Assert.False(rig.Engine.BitePointState.Holding);
    }

    [Fact]
    public void RemovingAnUnrelatedControllerOrKeyboardPreservesHeldFullClutchOutput()
    {
        var rig = new Rig();
        rig.Configure(.4, mode: ClutchBitePointMode.HoldFullRange);
        rig.Engine.SetBitePointEnabled(true);
        rig.Buttons.Set(Rig.Button, true); rig.Poll(); rig.Advance(250, 16384);
        rig.Buttons.RemoveDevice("other-wheel"); rig.Poll();
        Assert.True(rig.Engine.BitePointState.Holding);
        Assert.Equal(1, rig.Sink.Last.Clutch);
        rig.Buttons.NotifyDeviceRemoved(); rig.Poll();
        Assert.True(rig.Engine.BitePointState.Holding);
        Assert.Equal(1, rig.Sink.Last.Clutch);
        rig.Buttons.RemoveDevice("wheel"); rig.Poll();
        Assert.False(rig.Engine.BitePointState.Holding);
        Assert.Equal(.4, rig.Sink.Last.Clutch);
    }

    [Fact]
    public void RemovingAButtonDeviceCannotCompleteAPendingDoubleTap()
    {
        var rig = new Rig();
        rig.Tap();
        rig.Buttons.RemoveDevice("wheel");
        rig.Buttons.Set(Rig.Button, true); rig.Poll();
        Assert.False(rig.Engine.BitePointState.Enabled);
    }

    [Fact]
    public void BitePointIsAppliedAfterFilteringDeadzonesAndCurveWithoutResettingThem()
    {
        var axis = new AxisProfile(new(0, 16384), new(.1, .1), ProfileCurve.Power(2), filter: new(.1));
        var rig = new Rig(axis);
        rig.Engine.SetBitePointEnabled(true);
        rig.Buttons.Set(Rig.Button, true); rig.Poll(); rig.Advance(250);
        rig.Clock.Ticks += 10; rig.Sample(8192);
        var before = rig.Engine.Snapshot(PedalAxis.Clutch).Axes.Single(a => a.Axis == PedalAxis.Clutch).Result!.Value.Processing;
        Assert.InRange(before.FilteredNormalized, 0, .5);
        Assert.Equal(Math.Pow(Math.Clamp((before.FilteredNormalized - .1) / .8, 0, 1), 2), before.Output, 10);
        Assert.Equal(.4, rig.Sink.Last.Clutch);
        rig.Configure(.6);
        rig.Clock.Ticks += 10; rig.Sample(8192);
        var after = rig.Engine.Snapshot(PedalAxis.Clutch).Axes.Single(a => a.Axis == PedalAxis.Clutch).Result!.Value.Processing;
        Assert.False(after.FilterReset);
        Assert.InRange(after.FilteredNormalized, before.FilteredNormalized, .2);
        Assert.Equal(.6, rig.Sink.Last.Clutch);
        Assert.Equal(0, rig.Sink.Invalidations);
    }

    [Fact]
    public void RawSensorMappingKeepsCalibrationAndBiteSettingsIndependent()
    {
        var bounds = new PedalCalibration(new(1000, 15000), new(3200500, 4700500), new(2000, 14000));
        var calibration = new SensorCalibration("pedals", bounds);
        var original = new AxisProfile(new(0, 16384), bitePoint: new(.4, Rig.Button));
        var mapped = calibration.Map(PedalAxis.Clutch, original);
        Assert.Equal(original.BitePoint, mapped.BitePoint);
        Assert.Equal(2000, mapped.Calibration.Minimum);
        Assert.Equal(14000, mapped.Calibration.Maximum);
        Assert.Equal(0, mapped.CreateProcessor().Process(2000).Output);
        Assert.Equal(bounds, calibration.Bounds);
    }

    [Fact]
    public void CapturesKeepCurveOutputAndActualAssistedOutputSeparately()
    {
        var rig = new Rig();
        var recording = new Recording();
        rig.Engine.AttachRecording(recording);
        rig.Engine.SetBitePointEnabled(true);
        rig.Buttons.Set(Rig.Button, true); rig.Poll(); rig.Advance(260);
        var sample = recording.Last!;
        Assert.Equal(0, sample.Clutch.Processing.Output);
        Assert.Equal(.4, sample.FinalClutchOutput);
        Assert.Equal("0.4", RecordingFormat.Processed(1, sample).Split(',')[^1]);
        Assert.Contains("clutch-bite-point", recording.Events);
    }

    [Fact]
    public void FullRangeModeUsesTheConfiguredRangeImmediatelyOnReleaseWithoutPedalMovement()
    {
        var rig = new Rig();
        rig.Configure(.4, mode: ClutchBitePointMode.HoldFullRange);
        rig.Sample(16384);
        Assert.Equal(1, rig.Sink.Last.Clutch);
        rig.Engine.SetBitePointEnabled(true);
        Assert.Equal(.4, rig.Sink.Last.Clutch);
        rig.Buttons.Set(Rig.Button, true); rig.Poll();
        rig.Advance(249, 16384);
        Assert.Equal(.4, rig.Sink.Last.Clutch);
        rig.Advance(1, 16384);
        Assert.Equal(1, rig.Sink.Last.Clutch);
        rig.Buttons.Set(Rig.Button, false); rig.Poll();
        Assert.Equal(.4, rig.Sink.Last.Clutch);
        Assert.Equal(new PedalOutput(.25, .5, .4), rig.Sink.Last);
        rig.Sample(8192);
        Assert.Equal(.2, rig.Sink.Last.Clutch);
        rig.Sample(0);
        Assert.Equal(0, rig.Sink.Last.Clutch);
        Assert.Equal(0, rig.Sink.Invalidations);
    }

    [Fact]
    public void FullRangeModeChangesPercentageAndModeLiveAndCanReturnToNormalOutput()
    {
        var rig = new Rig();
        rig.Configure(.4, mode: ClutchBitePointMode.HoldFullRange);
        rig.Engine.SetBitePointEnabled(true);
        rig.Sample(8192);
        var before = rig.Engine.Snapshot(PedalAxis.Clutch);
        rig.Configure(.6, mode: ClutchBitePointMode.HoldFullRange);
        Assert.Equal(.3, rig.Sink.Last.Clutch);
        Assert.Equal(before.SampleCount, rig.Engine.Snapshot(PedalAxis.Clutch).SampleCount);
        Assert.Equal(.5, rig.Engine.Snapshot(PedalAxis.Clutch).Axes.Single(axis => axis.Axis == PedalAxis.Clutch).Result!.Value.Processing.Output);
        rig.Configure(.6);
        Assert.Equal(.5, rig.Sink.Last.Clutch);
        rig.Configure(.6, mode: ClutchBitePointMode.HoldFullRange);
        rig.Engine.SetBitePointEnabled(false);
        Assert.Equal(.5, rig.Sink.Last.Clutch);
        rig.Engine.SetBitePointEnabled(true);
        Assert.Equal(.3, rig.Sink.Last.Clutch);
        rig.Configure(.6, bind: false, mode: ClutchBitePointMode.HoldFullRange);
        Assert.Equal(.5, rig.Sink.Last.Clutch);
        Assert.False(rig.Engine.BitePointState.Enabled);
        Assert.Equal(0, rig.Sink.Invalidations);
    }

    [Fact]
    public void QuickTapDoesNotPulseFullRangeAndDoubleTapStillDisablesTheMode()
    {
        var rig = new Rig();
        rig.Configure(.4, mode: ClutchBitePointMode.HoldFullRange);
        rig.Engine.SetBitePointEnabled(true); rig.Sample(16384);
        rig.Buttons.Set(Rig.Button, true); rig.Poll(); rig.Advance(30, 16384);
        Assert.Equal(.4, rig.Sink.Last.Clutch);
        rig.Buttons.Set(Rig.Button, false); rig.Poll();
        Assert.Equal(.4, rig.Sink.Last.Clutch);
        rig.Advance(50, 16384); rig.Buttons.Set(Rig.Button, true); rig.Poll();
        Assert.False(rig.Engine.BitePointState.Enabled);
        Assert.Equal(1, rig.Sink.Last.Clutch);
        Assert.Equal(0, rig.Sink.Invalidations);
    }

    [Fact]
    public void FullRangeModeResumesAHeldButtonAfterReconnectAndNeverAffectsReplayOrCalibration()
    {
        var rig = new Rig();
        rig.Configure(.4, mode: ClutchBitePointMode.HoldFullRange);
        rig.Engine.SetBitePointEnabled(true);
        rig.Buttons.Set(Rig.Button, true); rig.Poll(); rig.Advance(250, 16384);
        Assert.True(rig.Engine.BitePointState.Holding);
        rig.Engine.Begin(new("live-device-axis"));
        rig.Sample(16384); rig.Sample(16384); rig.Poll();
        Assert.Equal(.4, rig.Engine.Snapshot(PedalAxis.Clutch).Axes.Single(axis => axis.Axis == PedalAxis.Clutch).FinalOutput);
        rig.Advance(250, 16384);
        Assert.True(rig.Engine.BitePointState.Holding);
        Assert.Equal(1, rig.Engine.Snapshot(PedalAxis.Clutch).Axes.Single(axis => axis.Axis == PedalAxis.Clutch).FinalOutput);
        rig.Engine.StartCalibration(PedalAxis.Clutch); rig.Poll();
        Assert.False(rig.Engine.BitePointState.Holding);
        Assert.False(rig.Engine.CanPublishOutput);
        Assert.Equal(1, rig.Engine.Snapshot(PedalAxis.Clutch).Axes.Single(axis => axis.Axis == PedalAxis.Clutch).FinalOutput);
        rig.Engine.Begin(new("capture-replay")); rig.Sample(16384); rig.Poll();
        Assert.Equal(1, rig.Engine.Snapshot(PedalAxis.Clutch).Axes.Single(axis => axis.Axis == PedalAxis.Clutch).FinalOutput);
        Assert.False(rig.Engine.CanPublishOutput);
    }

    [Fact]
    public void CapturesKeepFullRangeModeScalingSeparateFromTheResponseCurve()
    {
        var rig = new Rig();
        rig.Configure(.4, mode: ClutchBitePointMode.HoldFullRange);
        var recording = new Recording(); rig.Engine.AttachRecording(recording);
        rig.Engine.SetBitePointEnabled(true); rig.Sample(8192);
        Assert.Equal(.5, recording.Last!.Clutch.Processing.Output);
        Assert.Equal(.2, recording.Last.FinalClutchOutput);
        Assert.Equal("0.2", RecordingFormat.Processed(1, recording.Last).Split(',')[^1]);
    }

    private sealed class Recording : IDashboardRecording
    {
        public RecordedSample? Last { get; private set; }
        public List<string> Events { get; } = [];
        public void RecordSample(RecordedSample sample) => Last = sample;
        public void RecordEvent(RecordedEvent item) => Events.Add(item.Kind);
        public void Complete(string reason) { }
    }

    private sealed class Rig
    {
        internal static readonly CorrectionButton Button = new("controller", 21, "wheel");
        public ManualClock Clock { get; } = new();
        public DashboardEngine Engine { get; }
        public ButtonStateBuffer Buttons { get; } = new();
        public Sink Sink { get; } = new();
        private readonly AxisProfile clutchProfile;
        private readonly Dictionary<PedalAxis, AxisSessionSettings> limits = Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, _ => new AxisSessionSettings(1000, .05));
        public Rig(AxisProfile? clutch = null)
        {
            clutchProfile = clutch ?? new(new(0, 16384));
            Engine = new(Clock); Configure(.4); Engine.Begin(new("live-device-axis"));
            Sample(0); Sample(0); Engine.AttachOutput(Sink);
        }
        public void Configure(double output, bool bind = true, ClutchBitePointMode mode = ClutchBitePointMode.HoldMinimum, string name = "Launch")
        {
            var axes = DashboardEngine.DefaultProfile().Axes.ToDictionary(pair => pair.Key, pair => pair.Value);
            axes[PedalAxis.Clutch] = clutchProfile.WithBitePoint(new(output, bind ? Button : null, mode));
            Engine.Configure(new(name, axes), limits);
        }
        public void Sample(int clutch) => Engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(Clock.Ticks), 1, new(4096, 8388608, clutch)));
        public void Poll() => Engine.ProcessCorrectionButtons(Buttons);
        public void Advance(int milliseconds, int clutch = 0)
        {
            for (var i = 0; i < milliseconds; i += 10) { Clock.Ticks += Math.Min(10, milliseconds - i); Sample(clutch); Poll(); }
        }
        public void Tap() { Buttons.Set(Button, true); Poll(); Advance(30); Buttons.Set(Button, false); Poll(); }
    }
    private sealed class Sink : IProcessedOutputSink
    {
        public PedalOutput Last { get; private set; }
        public int Invalidations { get; private set; }
        public void Submit(PedalOutput output) => Last = output;
        public void Invalidate(string reason) => Invalidations++;
    }
    private sealed class ManualClock : TimeProvider
    {
        public long Ticks { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
    }
}
