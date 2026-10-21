using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class GraphInteractionTests
{
    [Fact]
    public void WindowDefaultsToTenSecondsAndLongerWindowsRetainActualHistory()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        Assert.Equal(10, model.GraphWindow);
        model.GraphWindow = 30;
        for (var i = 0; i <= 1400; i++)
        {
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(i * .025), 1, new(i % 100, i % 100, 0)));
        }
        model.Refresh();
        var graph = SignalGraph.Prepare(model.PedalHistories, "Brake", "Input", false, model.GraphWindow);
        Assert.Equal(30, graph.End - graph.Start);
        Assert.False(graph.Lanes[0].Samples[0].IsPadding);
        foreach (var invalid in new[] { 0, -.1, double.NaN, double.PositiveInfinity, 60.1 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => model.GraphWindow = invalid);
            Assert.Equal(30, model.GraphWindow);
        }
    }

    [Fact]
    public void FiltersApplyAutomaticallyAndInvalidEditsKeepTheLastValidSettings()
    {
        var model = new DashboardViewModel(new DashboardEngine());
        Assert.False(model.HasFilterDraft);
        model.SetFilterPreset("Smooth");
        Assert.False(model.HasFilterDraft);
        model.Apply();
        Assert.False(model.HasFilterDraft);
        Assert.Empty(model.ValidationMessage);
        model.Editor.FilterMilliseconds = "20.0";
        Assert.False(model.HasFilterDraft);
        model.Editor.FilterMilliseconds = "25";
        model.RefreshUnsavedState();
        Assert.False(model.HasFilterDraft);
        model.SelectedAxis = PedalAxis.Throttle;
        Assert.False(model.HasFilterDraft);
        model.SelectedAxis = PedalAxis.Brake;
        Assert.False(model.HasFilterDraft);
        model.Editor.FilterMilliseconds = "invalid";
        Assert.True(model.HasFilterDraft);
        Assert.Throws<ArgumentException>(model.Apply);
        Assert.True(model.HasFilterDraft);
        model.SetFilterPreset("Smooth");
        Assert.False(model.HasFilterDraft);
    }

    [Fact]
    public void StartupUsesACompleteWindowOfDisplayOnlyZeros()
    {
        PedalHistory[] history = [new(PedalAxis.Brake, [], "Off")];
        var empty = SignalGraph.Prepare(history, "All pedals", "Input vs filtered", false, 6);
        Assert.Equal(-6, empty.Start);
        Assert.Equal(0, empty.End);
        Assert.All(empty.Lanes[0].Samples, sample => { Assert.True(sample.IsPadding); Assert.Equal(0, sample.Output); });
        history[0] = new(PedalAxis.Brake, [new(1.5, .4, .3, .2)], "Balanced");
        var started = SignalGraph.Prepare(history, "All pedals", "Input vs filtered", false, 6);
        Assert.Equal(6, started.End - started.Start);
        Assert.Equal(started.Start, started.Lanes[0].Samples[0].Time);
        Assert.Equal(0, started.Lanes[0].Samples[1].Output);
        Assert.Equal(.2, started.Lanes[0].Samples[^1].Output);
        Assert.Single(history[0].Samples);
    }

    [Fact]
    public void PauseFreezesOnlyGraphWhileProcessingAndMetersContinue()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, 0, 0)));
        model.Refresh();
        var frozen = model.PedalHistories;
        model.GraphPaused = true;
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.03), 1, new(16384, 8388608, 0)));
        model.Refresh();
        Assert.Same(frozen, model.PedalHistories);
        Assert.Equal(100, model.Cards[0].OutputPercent);
        Assert.Equal(2, engine.Snapshot(PedalAxis.Brake).SampleCount);
        model.GraphPaused = false;
        model.Refresh();
        Assert.NotSame(frozen, model.PedalHistories);
        Assert.Equal(.03, model.PedalHistories[0].Samples[^1].Time);
    }

    [Fact]
    public void SelectingAPedalFollowsOnlyWhenGraphIsInSinglePedalMode()
    {
        var model = new DashboardViewModel(new DashboardEngine());
        model.SelectedAxis = PedalAxis.Throttle;
        Assert.Equal("All pedals", model.GraphPedal);
        model.GraphPedal = "Brake";
        model.SelectedAxis = PedalAxis.Throttle;
        Assert.Equal("Throttle", model.GraphPedal);
        model.SelectedAxis = PedalAxis.Clutch;
        Assert.Equal("Clutch", model.GraphPedal);
    }

    [Fact]
    public void MouseViewportAnchorsZoomAndClampsSelectionsToCapturedWindow()
    {
        var view = new GraphViewport(-6, 0);
        view.Zoom(.5, 120);
        Assert.Equal(4.5, view.Duration);
        Assert.Equal(-3, (view.Start + view.End) / 2);
        var before = view.Duration;
        view.Select(.8, .2);
        Assert.Equal(before * .6, view.Duration, 10);
        for (var i = 0; i < 100; i++) { view.Zoom(1, 120); }
        Assert.InRange(view.Duration, .099999, .100001);
        for (var i = 0; i < 100; i++) { view.Zoom(0, -120); }
        Assert.Equal(-6, view.Start);
        Assert.Equal(0, view.End);
    }

    [Fact]
    public void ComparisonContainsFilteredAndCurvedValuesBeforeAndAfterMapping()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        model.SetCurvePreset(PedalAxis.Brake, "Progressive");
        model.Apply();
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, 8388608, 0)));
        model.Refresh();
        var frame = SignalGraph.Prepare(model.PedalHistories, "Brake", "Filtered vs curved", true);
        var actual = frame.Lanes[0].Samples.Last(sample => !sample.IsPadding);
        Assert.Equal(.5, actual.Filtered);
        Assert.Equal(.25, SignalGraph.Value(actual, "Filtered vs curved"));
        Assert.True(frame.Lanes[0].Minimum < .25 && frame.Lanes[0].Maximum > .5);
    }

    [Fact]
    public void CalibrationUsesTheFullCapturedRangeAndAppliesWhenDone()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        Assert.Throws<InvalidOperationException>(model.AdvanceCalibration);
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, 100, 0)));
        model.AdvanceCalibration();
        Assert.True(model.IsCalibrating);
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.002), 1, new(0, 100, 0)));
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.01), 1, new(0, 500, 0)));
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.02), 1, new(0, 300, 0)));
        Assert.True(engine.Snapshot(PedalAxis.Brake).Axes[1].Result!.Value.Processing.Output < .01);
        model.AdvanceCalibration();
        Assert.False(model.IsCalibrating);
        Assert.Equal("100", model.Editor.Minimum);
        Assert.Equal("500", model.Editor.Maximum);
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.03), 1, new(0, 300, 0)));
        Assert.Equal(.5, engine.Snapshot(PedalAxis.Brake).Axes[1].Result!.Value.Processing.Output);
        model.AdvanceCalibration();
        model.SelectedAxis = PedalAxis.Throttle;
        Assert.False(model.IsCalibrating);
    }
}
