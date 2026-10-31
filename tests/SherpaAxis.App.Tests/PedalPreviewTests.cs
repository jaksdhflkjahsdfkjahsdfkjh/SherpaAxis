using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class PedalPreviewTests
{
    [Fact]
    public void LightweightSnapshotsPreserveLatestOutputsWithoutCopyingOrConsumingGraphHistory()
    {
        var engine = new DashboardEngine(); engine.Begin();
        for (var i = 0; i < 150; i++) { Sample(engine, i); }
        var full = engine.Snapshot(PedalAxis.Brake);
        var light = engine.Snapshot(PedalAxis.Brake, includeHistory: false);
        Assert.NotEmpty(full.History);
        Assert.Empty(light.History);
        foreach (var axis in full.Axes)
        {
            var current = light.Axes.Single(item => item.Axis == axis.Axis);
            Assert.Equal(axis.Result, current.Result);
            Assert.Equal(axis.FinalOutput, current.FinalOutput);
            Assert.Equal(axis.IsFresh, current.IsFresh);
            Assert.Empty(current.History!);
        }
        Assert.Equal(full.History, engine.Snapshot(PedalAxis.Brake).History);
    }

    [Fact]
    public void IdenticalCardValuesDoNotRetriggerBindings()
    {
        var card = new AxisCardViewModel(PedalAxis.Throttle);
        card.UpdateControllerInput(4096, false);
        var changes = new List<string?>();
        card.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        for (var index = 0; index < 100; index++) { card.UpdateControllerInput(4096, false); }
        Assert.Empty(changes);
        card.UpdateControllerInput(8192, false);
        Assert.Equal(50, card.OutputPercent);
        Assert.Contains(nameof(AxisCardViewModel.OutputPercent), changes);
        Assert.DoesNotContain(nameof(AxisCardViewModel.IsFresh), changes);
    }

    [Fact]
    public void CardRefreshContinuesWithAPausedGraphAndKeepsItsFrozenHistory()
    {
        var engine = new DashboardEngine(); var model = new DashboardViewModel(engine);
        engine.Begin(); Sample(engine, 0); Sample(engine, 1); model.Refresh();
        model.GraphPaused = true;
        var history = model.History;
        Sample(engine, 200);
        model.RefreshCards();
        Assert.Same(history, model.History);
        var current = engine.Snapshot(PedalAxis.Brake, includeHistory: false).Axes.Single(axis => axis.Axis == PedalAxis.Brake);
        Assert.Equal(current.FinalOutput!.Value * 100, model.Cards.Single(card => card.Axis == PedalAxis.Brake).OutputPercent);
    }

    internal static void Sample(DashboardEngine engine, int index) => engine.Accept(new(PedalConnectionState.Connected,
        TimeSpan.FromMilliseconds(index * 10), 1, new(4096, index * 20000, 8192)));
}

[Collection("WPF UI")]
public sealed class PedalPreviewUiTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public Task PreviewRefreshFollowsRenderingUpTo144HertzWithoutRefreshingTheGraph(int displayHz) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new DashboardEngine();
            await using var main = new MainWindow(engine, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: new(directory))
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Normal };
            await main.InitializeForLaunchAsync(); main.Show();
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            engine.Begin(); PedalPreviewTests.Sample(engine, 0); PedalPreviewTests.Sample(engine, 1); model.Refresh();
            var history = model.History;
            var card = model.Cards.Single(item => item.Axis == PedalAxis.Brake);
            var updates = 0;
            card.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AxisCardViewModel.OutputPercent)) { updates++; } };
            for (var index = 0; index <= displayHz; index++)
            {
                PedalPreviewTests.Sample(engine, index + 2);
                main.RefreshPedalPreviews(TimeSpan.FromSeconds((double)index / displayHz));
                PedalPreviewTests.Sample(engine, index + 3);
                main.RefreshPedalPreviews(TimeSpan.FromSeconds((double)index / displayHz));
            }
            var expectedRate = Math.Min(displayHz, 144);
            Assert.InRange(updates, expectedRate - 1, expectedRate + 2);
            Assert.Same(history, model.History);
            var previous = card.OutputPercent;
            main.Hide();
            PedalPreviewTests.Sample(engine, 250);
            main.RefreshPedalPreviews(TimeSpan.FromSeconds(1.1));
            Assert.Equal(previous, card.OutputPercent);
            main.Show(); main.RefreshPedalPreviews(TimeSpan.FromSeconds(1.2));
            Assert.True(card.OutputPercent > previous);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            main.UpdateLayout();
            var meter = WorkspaceSpacingTests.Descendants(main).OfType<ProgressBar>().Single(bar =>
                bar.DataContext is AxisCardViewModel { Axis: PedalAxis.Brake });
            var track = Assert.IsAssignableFrom<FrameworkElement>(meter.Template.FindName("PART_Track", meter));
            var indicator = Assert.IsAssignableFrom<FrameworkElement>(meter.Template.FindName("PART_Indicator", meter));
            var expectedWidth = track.ActualWidth * card.OutputPercent / 100;
            Assert.InRange(indicator.ActualWidth, expectedWidth - 1, expectedWidth + 1);
            await main.DisposeAsync();
            previous = card.OutputPercent;
            PedalPreviewTests.Sample(engine, 350);
            main.RefreshPedalPreviews(TimeSpan.FromSeconds(1.4));
            Assert.Equal(previous, card.OutputPercent);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });
}
