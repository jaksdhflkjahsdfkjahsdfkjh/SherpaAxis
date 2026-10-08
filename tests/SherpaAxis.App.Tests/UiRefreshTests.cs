using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class UiRefreshTests
{
    [Fact]
    public void LiveRefreshDoesNotRebindEditorsSelectionsOrStaticControlSources()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        var notifications = new List<string?>();
        model.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        model.Editor.Minimum = "12.";
        for (var i = 0; i < 100; i++)
        {
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(i * .002), 1, new(i, i * 100, i * 2)));
            model.Refresh();
        }
        Assert.DoesNotContain(notifications, n => string.IsNullOrEmpty(n));
        Assert.DoesNotContain(nameof(model.Editor), notifications);
        Assert.DoesNotContain(nameof(model.SelectedProfile), notifications);
        Assert.DoesNotContain(nameof(model.ProfileTabs), notifications);
        Assert.DoesNotContain(nameof(model.SelectedAxis), notifications);
        Assert.DoesNotContain(nameof(model.CanEdit), notifications);
        Assert.Equal("12.", model.Editor.Minimum);
        Assert.Equal(3, model.PedalHistories.Count);
        Assert.All(model.PedalHistories, history => Assert.NotEmpty(history.Samples));
        Assert.NotEqual(model.PedalHistories[0].Samples[^1].DeviceInput, model.PedalHistories[1].Samples[^1].DeviceInput);
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Sharp")]
    [InlineData("Balanced")]
    [InlineData("Smooth")]
    [InlineData("Spike guard")]
    public void FilterPresetsAreDraftsPersistAndOnlyAffectSelectedPedal(string preset)
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine) { SelectedAxis = PedalAxis.Clutch };
        model.SetFilterPreset(preset);
        Assert.Equal(preset, model.FilterPreset);
        var draft = model.ValidateEditors().Profile;
        Assert.Null(draft.Axes[PedalAxis.Brake].Filter);
        Assert.Null(draft.Axes[PedalAxis.Throttle].Filter);
        Assert.Equal(preset, FilterPresets.Identify(AxisEditor.FromProfile(ProfileJson.Deserialize(ProfileJson.Serialize(draft)).Axes[PedalAxis.Clutch])));
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, 0, 0)));
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.002), 1, new(0, 0, 16384)));
        Assert.Equal(1, engine.Snapshot(PedalAxis.Clutch).Axes[2].Result!.Value.Processing.Output);
        model.Apply();
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.004), 1, new(0, 0, 0)));
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.006), 1, new(0, 0, 16384)));
        Assert.Equal(preset != "Off", engine.Snapshot(PedalAxis.Clutch).Axes[2].Result!.Value.Processing.Output < 1);
    }

    [Fact]
    public void GraphUsesOneTimeWindowAndSeparatesCountScalesAndPureFilterDifference()
    {
        PedalHistory[] series =
        [
            new(PedalAxis.Throttle, [new(0, 0, 0, 0), new(15, .3, .2, .7, 3000, .2)], "Off"),
            new(PedalAxis.Brake, [new(15, .5, .4, .8, 500000, .5)], "Balanced")
        ];
        var frame = SignalGraph.Prepare(series, "All pedals", "Input vs filtered", false);
        Assert.Equal(3, frame.Start);
        Assert.Equal(15, frame.End);
        Assert.Equal(2, frame.Lanes.Count);
        Assert.All(frame.Lanes, lane => { Assert.Single(lane.Samples); Assert.Equal(0, lane.Minimum); Assert.Equal(1, lane.Maximum); });
        var counts = SignalGraph.Prepare(series, "All pedals", "Device counts", false);
        Assert.True(counts.Lanes[0].Maximum < counts.Lanes[1].Minimum);
        var difference = SignalGraph.Prepare(series, "Throttle", "Filter difference", false);
        Assert.Equal(0, SignalGraph.Value(difference.Lanes[0].Samples[0], "Filter difference"));
        Assert.Equal(-difference.Lanes[0].Minimum, difference.Lanes[0].Maximum);
        Assert.True(difference.Lanes[0].Maximum > 0);
        Assert.Equal(.7, SignalGraph.Value(frame.Lanes[0].Samples[0], "Output"));
        var zoom = SignalGraph.Prepare(series, "Brake", "Input vs filtered", true);
        Assert.True(zoom.Lanes[0].Minimum < .4 && zoom.Lanes[0].Maximum > .5);
        Assert.Equal(14, SignalGraph.Prepare(series, "All pedals", "Output", false, 1).Start);
        Assert.Throws<ArgumentOutOfRangeException>(() => SignalGraph.Prepare(series, "All pedals", "Output", false, 0));
    }
}
