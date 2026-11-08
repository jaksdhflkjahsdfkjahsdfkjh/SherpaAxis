using System.IO;
using System.Windows;
using System.Windows.Controls;
using SherpaAxis.App.Controls;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class TelemetryGraphTests
{
    [Fact]
    public void ReductionPreservesShortExtremaForAllThreeSignalStages()
    {
        var points = Enumerable.Range(0, 6001).Select(index => new PlotSample(index / 100d, .2,
            index == 2413 ? .88 : .3, index == 4177 ? .97 : .4, 0, index == 613 ? .99 : .2)).ToArray();
        var reduced = TelemetryGraph.Window(points, 0, 60);
        Assert.InRange(reduced.Length, 2, 1000);
        Assert.Equal(points[0], reduced[0]); Assert.Equal(points[^1], reduced[^1]);
        Assert.Contains(reduced, point => SignalGraph.Input(point) == .99);
        Assert.Contains(reduced, point => point.Filtered == .88);
        Assert.Contains(reduced, point => point.Output == .97);
        Assert.Equal(reduced.OrderBy(point => point.Time), reduced);
    }

    [Fact]
    public void AOneMillisecondRawSpikeSurvivesTheCaptureBufferWhileTheFilterRemovesIt()
    {
        var engine = new DashboardEngine(); var model = new DashboardViewModel(engine);
        model.SelectedAxis = PedalAxis.Brake; model.SetFilterPreset("Spike guard");
        engine.Begin(new("live-device-axis")); engine.SetTelemetryCapture(true);
        for (var i = 0; i <= 1000; i++)
        {
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(i), 1, new(0, i == 13 ? 16777215 : 0, 0)));
        }
        var samples = engine.TelemetrySnapshot(1)!.Series.Single(series => series.Axis == PedalAxis.Brake).Samples;
        Assert.Equal(1, samples.Max(SignalGraph.Input), 6);
        Assert.True(samples.Max(sample => sample.Filtered) < .1);
        Assert.True(samples.Max(sample => sample.Output) < .1);
    }

    [Fact]
    public void SixtySecondTelemetryIsIndependentOfTheMainGraphsWindowAndNeverPersistsSamples()
    {
        var engine = new DashboardEngine(); engine.Begin(new("live-device-axis"));
        for (var i = 0; i < 100; i++) { Feed(engine, i); }
        Assert.All(engine.TelemetrySnapshot(60)!.Series, series => Assert.Empty(series.Samples));
        engine.SetTelemetryCapture(true);
        for (var i = 100; i <= 4000; i++) { Feed(engine, i); }
        engine.SetGraphWindow(1);
        var main = engine.Snapshot(PedalAxis.Brake);
        var telemetry = engine.TelemetrySnapshot(60)!;
        Assert.Equal(3, telemetry.Series.Count);
        foreach (var series in telemetry.Series)
        {
            Assert.InRange(series.Samples.Count, 2, 1000);
            Assert.True(series.Samples[0].Time <= telemetry.End - 59);
            Assert.All(series.Samples, sample => Assert.InRange(sample.Time, telemetry.End - 60, telemetry.End));
        }
        Assert.Equal(main.History, engine.Snapshot(PedalAxis.Brake).History);
        Assert.InRange(main.History.Count, 1, 600);
        Assert.True(main.History[0].Time > telemetry.End - 60);
        engine.SetTelemetryCapture(false);
        Assert.All(engine.TelemetrySnapshot(60)!.Series, series => Assert.Empty(series.Samples));
        Assert.Equal(main.History, engine.Snapshot(PedalAxis.Brake).History);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("profile")]
    [InlineData("gap")]
    public void BoundariesClearOldTelemetryAndInvalidateAPausedSnapshot(string boundary)
    {
        var engine = new DashboardEngine(); engine.Begin(new("live-device-axis")); engine.SetTelemetryCapture(true);
        for (var i = 0; i < 100; i++) { Feed(engine, i); }
        var before = engine.TelemetrySnapshot(60)!;
        Assert.Contains(before.Series, series => series.Samples.Count > 0);
        Assert.Null(engine.TelemetrySnapshot(60, before.Generation));
        if (boundary == "connection") { engine.Accept(new(PedalConnectionState.Waiting, TimeSpan.FromSeconds(3), 1, null)); }
        else if (boundary == "gap") { engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(20), 1, new(0, 0, 0))); }
        else
        {
            var profile = DashboardEngine.DefaultProfile();
            engine.Configure(new("New profile", profile.Axes), Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, _ => new SherpaAxis.Core.AxisSessionSettings(1000, .05)));
        }
        var after = engine.TelemetrySnapshot(60, before.Generation)!;
        Assert.NotEqual(before.Generation, after.Generation);
        Assert.All(after.Series, series => Assert.DoesNotContain(series.Samples, point => point.Time < (boundary == "profile" ? before.End : 2)));
    }

    [Fact]
    public Task GraphPauseAndSignalChoicesDoNotPauseOutputOrChangeTheMainGraph() => WithWindow(async (main, engine, view) =>
    {
        var model = (CompactPedalViewModel)view.DataContext;
        for (var i = 100; i < 200; i++) { Feed(engine, i); }
        view.RefreshFrame(TimeSpan.FromSeconds(1));
        var mainHistory = engine.Snapshot(PedalAxis.Brake).History;
        model.Settings.GraphPaused = true;
        var frozen = model.GraphFrame;
        for (var i = 200; i < 300; i++) { Feed(engine, i); }
        view.RefreshFrame(TimeSpan.FromSeconds(2));
        Assert.Same(frozen, model.GraphFrame);
        Assert.True(engine.CompactSnapshot().Live);
        model.Settings.WindowSeconds = 60; model.Settings.ShowRaw = true; model.Settings.ShowFiltered = true; model.Settings.ShowOutput = false;
        var plot = Assert.IsType<CombinedPedalPlot>(view.FindName("TelemetryPlot"));
        view.UpdateLayout();
        Assert.True(plot.ShowRaw); Assert.True(plot.ShowFiltered); Assert.False(plot.ShowOutput); Assert.Equal(60, plot.WindowSeconds);
        Assert.Same(frozen, model.GraphFrame);
        Assert.True(engine.Snapshot(PedalAxis.Brake).History.Count >= mainHistory.Count);
        model.Settings.GraphPaused = false; view.RefreshFrame(TimeSpan.FromSeconds(3));
        Assert.True(model.GraphFrame!.End > frozen!.End);
        model.Settings.GraphPaused = true;
        engine.Accept(new(PedalConnectionState.Waiting, TimeSpan.FromSeconds(8), 1, null));
        view.RefreshFrame(TimeSpan.FromSeconds(4));
        Assert.NotSame(frozen, model.GraphFrame);
        Assert.All(model.GraphFrame!.Series, series => Assert.Empty(series.Samples));
        await Task.CompletedTask;
    });

    [Fact]
    public Task SettingsResizeDisplayAndCloseReleasesOnlyTelemetry() => WithWindow(async (main, engine, view) =>
    {
        var settings = ((CompactPedalViewModel)view.DataContext).Settings;
        settings.Width = 800; settings.Height = 200;
        Assert.Equal(Math.Min(800, SystemParameters.WorkArea.Width), view.Width);
        Assert.Equal(Math.Min(200, SystemParameters.WorkArea.Height), view.Height);
        Assert.Equal(WindowStyle.None, view.WindowStyle);
        Assert.Equal(ResizeMode.NoResize, view.ResizeMode);
        var history = engine.Snapshot(PedalAxis.Brake).History;
        Assert.Same(view, main.OpenCompactView());
        view.Close();
        Assert.All(engine.TelemetrySnapshot(60)!.Series, series => Assert.Empty(series.Samples));
        Assert.Equal(history, engine.Snapshot(PedalAxis.Brake).History);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData("en", 640, 140)]
    [InlineData("uk", 640, 140)]
    [InlineData("en", 360, 110)]
    [InlineData("uk", 360, 110)]
    public Task CleanDisplayHasNoConfigurationControlsAndFitsBothLanguages(string language, double width, double height)
        => WithWindow(async (_, engine, view) =>
        {
            try
            {
                Localizer.Instance.Language = language;
                var model = (CompactPedalViewModel)view.DataContext;
                model.Settings.Width = width; model.Settings.Height = height;
                model.Settings.ShowRaw = model.Settings.ShowFiltered = true;
                for (var i = 100; i < 600; i++) { Feed(engine, i); }
                view.RefreshFrame(TimeSpan.FromSeconds(5));
                var root = (FrameworkElement)view.Content;
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                var plot = Assert.IsType<CombinedPedalPlot>(view.FindName("TelemetryPlot"));
                Assert.True(plot.ActualHeight >= 80); Assert.True(plot.ActualWidth >= 180);
                Assert.Equal(3, plot.Frame!.Series.Count);
                Assert.True(plot.ShowRaw && plot.ShowFiltered && plot.ShowOutput);
                Assert.Empty(WorkspaceSpacingTests.Descendants(view).OfType<System.Windows.Controls.Primitives.ButtonBase>());
                Assert.Empty(WorkspaceSpacingTests.Descendants(view).OfType<Slider>());
                model.Settings.WindowSeconds = 5; root.UpdateLayout();
                Assert.Equal(5, plot.WindowSeconds);
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                Assert.True(root.ActualWidth <= width);
                UiRefinementTests.CapturePreview(root, "pedal-graph-" + language + (width < 500 ? "-small" : "") + ".png");
                model.Settings.ShowRaw = model.Settings.ShowFiltered = model.Settings.ShowOutput = false; root.UpdateLayout();
                UiRefinementTests.CapturePreview(root, "pedal-graph-empty-selection-" + language + ".png");
                await Task.CompletedTask;
            }
            finally { Localizer.Instance.Language = "en"; }
        });

    private static void Feed(DashboardEngine engine, int index)
    {
        var time = index * .02;
        var throttle = (ushort)(Math.Clamp(.45 + .35 * Math.Sin(time * 1.3), 0, 1) * 16384);
        var clutch = (ushort)(Math.Clamp(.35 + .3 * Math.Sin(time * .8 + 1.5), 0, 1) * 16384);
        var brake = (int)(Math.Clamp(.4 + .4 * Math.Sin(time * 1.8 + .5), 0, 1) * 16777215);
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(time), 1, new(throttle, brake, clutch)));
    }

    private static Task WithWindow(Func<MainWindow, DashboardEngine, CompactPedalWindow, Task> action) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new DashboardEngine();
            await using var main = new MainWindow(engine, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: new(folder));
            await main.InitializeForLaunchAsync();
            engine.Begin(new("live-device-axis"));
            var model = (DashboardViewModel)main.DataContext;
            foreach (var axis in Enum.GetValues<PedalAxis>())
            {
                model.SelectedAxis = axis; model.SetFilterPreset("Spike guard"); model.SetCurvePreset(axis, "Progressive");
            }
            for (var i = 0; i < 100; i++) { Feed(engine, i); }
            var view = main.OpenCompactView();
            await action(main, engine, view);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });
}
