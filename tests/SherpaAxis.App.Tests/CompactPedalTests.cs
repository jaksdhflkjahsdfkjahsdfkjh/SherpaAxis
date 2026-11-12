using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Input;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class CompactPedalTests
{
    [Fact]
    public Task PercentLabelsKeepTheMeterValuesAndShowOnlyWholePercentages() => WithMain(async (main, engine, clock, _) =>
    {
        engine.Begin(new("live-device-axis"));
        Feed(engine, clock, 0, 1131, 16777000, 16384);
        Feed(engine, clock, 10, 1131, 16777000, 16384);
        var overlay = main.OpenCompactView();
        overlay.RefreshNow(); overlay.UpdateLayout();
        UiRefinementTests.CapturePreview(Assert.IsAssignableFrom<FrameworkElement>(overlay.Content), "overlay-percentages.png");
        var cards = Assert.IsType<CompactPedalViewModel>(overlay.DataContext).Cards;
        Assert.InRange(cards.Single(card => card.Axis == PedalAxis.Throttle).OutputPercent, 6.9, 7);
        Assert.InRange(cards.Single(card => card.Axis == PedalAxis.Brake).OutputPercent, 99.9, 100);
        Assert.Equal(100, cards.Single(card => card.Axis == PedalAxis.Clutch).OutputPercent);
        foreach (var label in WorkspaceSpacingTests.Descendants(overlay).OfType<TextBlock>().Where(text => text.FontSize == 12 && text.DataContext is AxisCardViewModel))
        {
            var card = (AxisCardViewModel)label.DataContext;
            Assert.Equal(card.Axis switch { PedalAxis.Clutch => "100", PedalAxis.Brake => "99", _ => "6" }, label.Text);
        }
        Feed(engine, clock, 20, 16384, checked((int)DashboardEngine.DeviceMaximum(PedalAxis.Brake)), 0);
        overlay.RefreshNow(); overlay.UpdateLayout();
        foreach (var label in WorkspaceSpacingTests.Descendants(overlay).OfType<TextBlock>().Where(text => text.FontSize == 12 && text.DataContext is AxisCardViewModel))
        {
            Assert.Equal(((AxisCardViewModel)label.DataContext).Axis == PedalAxis.Clutch ? "0" : "100", label.Text);
        }
        clock.Ticks += 1000;
        overlay.RefreshNow(); overlay.UpdateLayout();
        Assert.All(WorkspaceSpacingTests.Descendants(overlay).OfType<TextBlock>().Where(text => text.FontSize == 12 && text.DataContext is AxisCardViewModel),
            label => Assert.Equal("—", label.Text));
        await Task.CompletedTask;
    });

    [Fact]
    public Task CompactViewIsSingleInstanceSurvivesMainHideAndClosesWithTheApp() => WithMain(async (main, _, _, _) =>
    {
        Assert.Empty(Application.Current?.Windows.OfType<CompactPedalWindow>() ?? []);
        await main.RequestPanelModeAsync(SidePanelMode.PedalOverlay);
        var button = Assert.IsType<CheckBox>(main.FindName("OverlayEnabledCheck"));
        button.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var compact = main.OpenCompactView();
        Assert.Same(compact, main.OpenCompactView());
        Assert.True(compact.Topmost); Assert.False(compact.ShowInTaskbar); Assert.Null(compact.Owner);
        Assert.False(compact.ShowActivated);
        main.ShowInTaskbar = false; main.Opacity = 0; main.Show();
        main.WindowState = WindowState.Minimized;
        Assert.True(compact.IsVisible); Assert.Equal(WindowState.Normal, compact.WindowState);
        Assert.True(await main.HideToTrayAsync());
        Assert.True(compact.IsVisible); Assert.False(main.IsVisible);
        compact.Close();
        var reopened = main.OpenCompactView();
        Assert.NotSame(compact, reopened);
        var closed = false; reopened.Closed += (_, _) => closed = true;
        await main.DisposeAsync();
        Assert.True(closed); Assert.False(reopened.IsVisible);
        Assert.Throws<InvalidOperationException>(main.OpenCompactView);
    });

    [Fact]
    public Task FinalClutchOutputAndBitePointStateUpdateWithoutRefreshingTheMainGraph() => WithMain(async (main, engine, clock, model) =>
    {
        ConfigureBite(model);
        engine.Begin(new("live-device-axis"));
        Feed(engine, clock, 0, 4096, 10401873, 16384); Feed(engine, clock, 10, 4096, 10401873, 16384);
        engine.SetBitePointEnabled(true);
        model.Refresh(); var history = model.History;
        var compact = main.OpenCompactView();
        var small = Assert.IsType<CompactPedalViewModel>(compact.DataContext);
        Assert.Equal(40, small.Cards.Single(card => card.Axis == PedalAxis.Clutch).OutputPercent, 6);
        Assert.Equal(25, small.Cards.Single(card => card.Axis == PedalAxis.Throttle).OutputPercent, 6);
        Assert.Contains("Limited range", small.BitePointInfo, StringComparison.Ordinal);
        using var buttons = new ButtonStateBuffer();
        engine.ProcessCorrectionButtons(buttons); buttons.Set(new("keyboard", 65), true);
        for (var time = 20; time <= 320; time += 50)
        {
            Feed(engine, clock, time, 8192, 10401873, 16384);
            engine.ProcessCorrectionButtons(buttons);
        }
        main.Hide(); compact.RefreshFrame(TimeSpan.FromSeconds(1));
        Assert.Equal(100, small.Cards.Single(card => card.Axis == PedalAxis.Clutch).OutputPercent);
        Assert.Contains("Full range", small.BitePointInfo, StringComparison.Ordinal);
        Assert.Equal(50, small.Cards.Single(card => card.Axis == PedalAxis.Throttle).OutputPercent, 6);
        Assert.Same(history, model.History);
        var full = engine.Snapshot(PedalAxis.Brake);
        Assert.NotEmpty(full.History);
        Assert.All(engine.CompactSnapshot().Axes, axis => Assert.Empty(axis.History!));
        Assert.Equal(full.History, engine.Snapshot(PedalAxis.Brake).History);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData("firmware")]
    [InlineData("recovery")]
    [InlineData("calibration")]
    [InlineData("disconnected")]
    [InlineData("stale")]
    public Task PausesAndLostInputClearTheMetersAndRecoverWithFreshInput(string state) => WithMain(async (main, engine, clock, model) =>
    {
        engine.Begin(new("live-device-axis")); Feed(engine, clock, 0, 8192, 8000000, 8192); Feed(engine, clock, 10, 8192, 8000000, 8192);
        var compact = main.OpenCompactView(); var small = (CompactPedalViewModel)compact.DataContext;
        Assert.True(small.IsLive); Assert.All(small.Cards, card => Assert.True(card.OutputPercent > 0));
        switch (state)
        {
            case "firmware": model.Firmware.Running = true; break;
            case "recovery": model.Recovery.Checking(); break;
            case "calibration": model.AdvanceCalibration(); break;
            case "disconnected": engine.Accept(new(PedalConnectionState.Waiting, TimeSpan.FromMilliseconds(20), 1, null)); break;
            case "stale": clock.Ticks += 300; break;
        }
        compact.RefreshNow();
        Assert.False(small.IsLive);
        Assert.All(small.Cards, card => { Assert.Equal(0, card.OutputPercent); Assert.Equal("—", card.OutputText); });
        model.Firmware.Running = false; model.Recovery.Apply([]); model.CancelCalibration();
        Feed(engine, clock, 400, 4096, 4000000, 4096); Feed(engine, clock, 410, 4096, 4000000, 4096);
        compact.RefreshNow();
        Assert.True(small.IsLive); Assert.All(small.Cards, card => Assert.True(card.OutputPercent > 0));
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public Task CompactMetersFollowRenderingUpTo144HzEvenWithTheMainWindowHidden(int hz) => WithMain(async (main, engine, clock, model) =>
    {
        engine.Begin(new("live-device-axis")); Feed(engine, clock, 0, 0, 0, 0); Feed(engine, clock, 1, 0, 0, 0);
        var compact = main.OpenCompactView(); var small = (CompactPedalViewModel)compact.DataContext;
        var card = small.Cards.Single(item => item.Axis == PedalAxis.Throttle);
        var updates = 0;
        card.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AxisCardViewModel.OutputPercent)) { updates++; } };
        var history = model.History;
        main.Hide();
        for (var index = 0; index <= hz; index++)
        {
            Feed(engine, clock, index + 2, (ushort)(index * 20 + 1), 0, 0);
            compact.RefreshFrame(TimeSpan.FromSeconds((double)index / hz));
            compact.RefreshFrame(TimeSpan.FromSeconds((double)index / hz));
        }
        Assert.InRange(updates, Math.Min(hz, 144) - 1, Math.Min(hz, 144) + 2);
        Assert.Same(history, model.History);
        compact.Close(); var last = card.OutputPercent;
        Feed(engine, clock, hz + 10, 12000, 0, 0); compact.RefreshFrame(TimeSpan.FromSeconds(2));
        Assert.Equal(last, card.OutputPercent);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task CompactLayoutShowsCorrectMetersAndFitsBothLanguages(string language) => WithMain(async (main, engine, clock, model) =>
    {
        try
        {
            Localizer.Instance.Language = language;
            ConfigureBite(model);
            engine.Begin(new("live-device-axis")); Feed(engine, clock, 0, 4096, 10401873, 16384); Feed(engine, clock, 10, 4096, 10401873, 16384);
            engine.SetBitePointEnabled(true);
            var compact = main.OpenCompactView();
            var root = Assert.IsType<Grid>(compact.Content);
            root.Measure(new Size(640, 140)); root.Arrange(new Rect(0, 0, 640, 140)); root.UpdateLayout();
            var meters = WorkspaceSpacingTests.Descendants(root).OfType<ProgressBar>().ToArray();
            Assert.Equal(3, meters.Length);
            foreach (var meter in meters)
            {
                meter.ApplyTemplate();
                var track = Assert.IsAssignableFrom<FrameworkElement>(meter.Template.FindName("PART_Track", meter));
                var indicator = Assert.IsAssignableFrom<FrameworkElement>(meter.Template.FindName("PART_Indicator", meter));
                Assert.True(track.ActualWidth > 45);
                Assert.InRange(indicator.ActualWidth, track.ActualWidth * meter.Value / 100 - 1, track.ActualWidth * meter.Value / 100 + 1);
                Assert.Equal(Orientation.Vertical, meter.Orientation);
            }
            Assert.Equal(Localizer.Translate("Bite point: 40% · Limited range"), Localizer.Translate(((CompactPedalViewModel)compact.DataContext).BitePointInfo));
            Assert.Contains(Localizer.Translate("Pedal overlay"), Assert.IsType<Button>(main.FindName("PedalOverlayModeButton")).Content.ToString(), StringComparison.Ordinal);
            UiRefinementTests.CapturePreview(root, "compact-view-" + language + ".png");
            var content = (FrameworkElement)main.Content;
            content.Measure(new Size(1056, 680)); content.Arrange(new Rect(0, 0, 1056, 680)); content.UpdateLayout();
            UiRefinementTests.CapturePreview(Assert.IsType<Border>(main.FindName("ProfileBar")), "compact-toolbar-" + language + ".png");
            await Task.CompletedTask;
        }
        finally { Localizer.Instance.Language = "en"; }
    });

    [Fact]
    public Task ProfileLabelUsesAppliedSettingsAndBrowsingCalibrationDoesNotPauseThePreview() => WithMain(async (main, engine, clock, model) =>
    {
        engine.Begin(new("live-device-axis")); Feed(engine, clock, 0, 4096, 4000000, 4096); Feed(engine, clock, 10, 4096, 4000000, 4096);
        var expected = engine.CompactSnapshot().ProfileName;
        var compact = main.OpenCompactView(); var small = (CompactPedalViewModel)compact.DataContext;
        model.Editor.Minimum = "unfinished"; model.ProfileName = "Draft not applied"; model.RefreshUnsavedState();
        model.PanelMode = SidePanelMode.Calibration;
        compact.RefreshNow(); Assert.True(small.IsLive);
        Assert.Equal(expected, small.ProfileName); Assert.NotEqual(model.ProfileName, small.ProfileName);
        await Task.CompletedTask;
    });

    private static void ConfigureBite(DashboardViewModel model)
    {
        model.SelectedAxis = PedalAxis.Clutch;
        model.Editor.BitePointOutput = "40";
        model.SetBitePointMode("Hold full range");
        model.BindBitePoint(new("keyboard", 65));
        model.ProfileName = "GT3 · Race";
        model.RefreshUnsavedState();
    }

    private static Task WithMain(Func<MainWindow, DashboardEngine, ManualClock, DashboardViewModel, Task> action) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-compact-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new ManualClock(); var engine = new DashboardEngine(clock);
            await using var main = new MainWindow(engine, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: new(folder));
            await main.InitializeForLaunchAsync();
            await action(main, engine, clock, (DashboardViewModel)main.DataContext);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });

    private static void Feed(DashboardEngine engine, ManualClock clock, long time, ushort throttle, int brake, ushort clutch)
    {
        clock.Ticks = time;
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(time), 1, new(throttle, brake, clutch)));
    }
    private sealed class ManualClock : TimeProvider
    {
        internal long Ticks { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
    }
}
