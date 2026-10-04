using System.Globalization;
using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class DashboardTests
{
    [Fact]
    public void EditorRoundTripsCustomProcessingSettingsAndUsesInvariantNumbers()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var profile = new AxisProfile(new(100.5, 200.5, true), new(.05, .1),
                ProfileCurve.PiecewiseLinear([new(0, 0), new(.5, .7), new(1, 1)]), 10, new(.01, .2));
            var editor = AxisEditor.FromProfile(profile);
            var restored = editor.BuildProfile();
            Assert.Equal(100.5, restored.Calibration.Minimum);
            Assert.True(restored.Calibration.Inverted);
            Assert.Equal(.05, restored.Deadzone.Lower);
            Assert.Equal(profile.Curve.Points, restored.Curve.Points);
            Assert.Equal(.01, restored.Filter!.TimeConstantSeconds);
            Assert.Equal(.2, restored.Filter.ResetAfterSeconds);
            Assert.Equal(10, restored.MaximumBaselineCorrection);
            Assert.Equal("100.5", editor.Minimum);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void InvalidEditsDoNotChangeTheActivePreviewOrDiscardOtherAxisDrafts()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        engine.Accept(Sample(0, 8000000));
        var before = engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result;
        model.Editor.Minimum = "200";
        model.Editor.Maximum = "100";
        model.SelectedAxis = PedalAxis.Throttle;
        model.Editor.FilterMilliseconds = "10";
        model.SelectedAxis = PedalAxis.Brake;
        Assert.Equal("200", model.Editor.Minimum);
        Assert.Throws<ArgumentException>(model.Apply);
        Assert.Equal(before, engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result);
        model.SelectedAxis = PedalAxis.Throttle;
        Assert.Equal("10", model.Editor.FilterMilliseconds);
    }

    [Fact]
    public void ApplyingAProfileClearsRuntimeHistoryAndKeepsObservedInputForCalibration()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        engine.Accept(Sample(0, 100));
        engine.Accept(Sample(.01, 200));
        model.UseObservedRange();
        Assert.Equal("100", model.Editor.Minimum);
        Assert.Equal("200", model.Editor.Maximum);
        model.Apply();
        var cleared = engine.Snapshot(PedalAxis.Brake);
        Assert.Empty(cleared.History);
        Assert.Null(cleared.Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result);
        engine.Accept(Sample(.02, 150));
        var frame = engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake);
        Assert.Equal(.5, frame.Result!.Value.Processing.Output);
        Assert.Equal(100, frame.ObservedMinimum);
        Assert.Equal(200, frame.ObservedMaximum);
    }

    [Fact]
    public void ReleasePermissionExpiresIfTheUiStopsRenewingIt()
    {
        var clock = new ManualClock();
        var engine = Configured(clock);
        engine.Accept(Sample(0, 95));
        engine.ConfirmBaseline(PedalAxis.Brake);
        engine.AuthorizeRelease(PedalAxis.Brake);
        clock.Advance(10);
        engine.Accept(Sample(.01, 95));
        var applied = Brake(engine).Processing.BaselineCorrection;
        Assert.Equal(-.1, applied, 12);
        clock.Advance(150);
        engine.Accept(Sample(.02, 105));
        Assert.Equal(applied, Brake(engine).Processing.BaselineCorrection);
        Assert.True(Brake(engine).CorrectionSuspended);
        engine.AuthorizeRelease(PedalAxis.Brake);
        clock.Advance(10);
        engine.Accept(Sample(.03, 95));
        Assert.True(Brake(engine).Processing.BaselineCorrection < applied);
        engine.RevokeRelease();
        var before = Brake(engine).Processing.BaselineCorrection;
        engine.Accept(Sample(.04, 110));
        Assert.Equal(before, Brake(engine).Processing.BaselineCorrection);
    }

    [Fact]
    public void StaleSamplesCannotAuthorizeOrCaptureABaseline()
    {
        var clock = new ManualClock();
        var engine = Configured(clock);
        var model = new DashboardViewModel(engine);
        engine.Accept(Sample(0, 95));
        clock.Advance(251);
        Assert.False(engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).IsFresh);
        Assert.Throws<InvalidOperationException>(() => engine.ConfirmBaseline(PedalAxis.Brake));
        Assert.Throws<InvalidOperationException>(() => model.CaptureEndpoint(false));
    }

    [Fact]
    public void DisconnectClearsValuesObservedExtremaAndReleasePermission()
    {
        var engine = Configured(new ManualClock());
        engine.Accept(Sample(0, 95));
        engine.ConfirmBaseline(PedalAxis.Brake);
        engine.AuthorizeRelease(PedalAxis.Brake);
        engine.Accept(Sample(.01, 95));
        engine.Accept(new(PedalConnectionState.Disconnected, TimeSpan.FromSeconds(.02), 1));
        var disconnected = engine.Snapshot(PedalAxis.Brake);
        Assert.Null(disconnected.Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result);
        Assert.Empty(disconnected.History);
        engine.Accept(Sample(.03, 150, 2));
        var next = Brake(engine);
        Assert.Equal(0, next.Processing.BaselineCorrection);
        Assert.Equal(0, next.TargetCorrection);
        Assert.False(next.ReleaseConfirmed);
        Assert.Equal(150, engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).ObservedMinimum);
    }

    [Fact]
    public void DisplayHistoryIsBoundedButEveryInputReportIsProcessed()
    {
        var engine = new DashboardEngine();
        for (var index = 0; index < 30000; index++)
        {
            engine.Accept(Sample(index * .002, index % 100));
        }

        var frame = engine.Snapshot(PedalAxis.Brake);
        Assert.Equal(30000, frame.SampleCount);
        Assert.InRange(frame.History.Count, 1, 600);
        Assert.Equal(0, frame.Axes.Single(axis => axis.Axis == PedalAxis.Brake).ObservedMinimum);
        Assert.Equal(99, frame.Axes.Single(axis => axis.Axis == PedalAxis.Brake).ObservedMaximum);
        Assert.Equal(500, frame.ReportsPerSecond, 10);
        Assert.All(frame.History, sample => Assert.Equal((int)Math.Round(sample.Time / .002) % 100, sample.DeviceInput));
    }

    [Fact]
    public void InvalidSettingsAndMalformedCustomCurvesAreRejected()
    {
        var editor = new AxisEditor { Maximum = "100", FilterMilliseconds = "NaN" };
        Assert.Throws<ArgumentException>(() => editor.BuildProfile());
        editor.FilterMilliseconds = "-1";
        Assert.Throws<ArgumentException>(() => editor.BuildProfile());
        editor.FilterMilliseconds = "10";
        editor.CurveKind = "Custom";
        editor.Points = "0:0; .8:.5; .2:.6; 1:1";
        Assert.Throws<ArgumentException>(() => editor.BuildProfile());
        editor.Points = "0:0; invalid; 1:1";
        Assert.Throws<ArgumentException>(() => editor.BuildProfile());
        editor.CorrectionRate = "0";
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.BuildSessionSettings());
    }

    [Fact]
    public void PartialProfilesUseExplicitDefaultsAndSavingDoesNotSerializeSessionState()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        model.Load(new("Brake", new Dictionary<PedalAxis, AxisProfile> { [PedalAxis.Brake] = new(new(100, 200), maximumBaselineCorrection: 10) }));
        model.Editor.CorrectionRate = "20";
        model.Apply();
        var (profile, _) = model.ValidateEditors();
        Assert.Equal(3, profile.Axes.Count);
        Assert.Equal(16384, profile.Axes[PedalAxis.Throttle].Calibration.Maximum);
        var restored = ProfileJson.Deserialize(ProfileJson.Serialize(profile));
        model.Load(restored);
        Assert.Equal("1000", model.Editor.CorrectionRate);
        Assert.Equal("10", model.Editor.MaximumCorrection);
    }

    private static DashboardEngine Configured(TimeProvider clock)
    {
        var engine = new DashboardEngine(clock);
        engine.Configure(new("Test", new Dictionary<PedalAxis, AxisProfile>
        {
            [PedalAxis.Brake] = new(new(100, 200), maximumBaselineCorrection: 10)
        }), Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, _ => new AxisSessionSettings(10, 1)));
        return engine;
    }

    private static PedalMonitorEvent Sample(double seconds, int brake, int connection = 1) =>
        new(PedalConnectionState.Connected, TimeSpan.FromSeconds(seconds), connection, new(0, brake, 0));

    private static AxisSessionResult Brake(DashboardEngine engine) =>
        engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result!.Value;

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public void Advance(long milliseconds) => timestamp += milliseconds;
    }
}
