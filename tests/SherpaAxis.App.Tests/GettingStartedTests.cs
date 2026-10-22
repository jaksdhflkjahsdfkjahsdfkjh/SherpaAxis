using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class GettingStartedTests
{
    private static readonly string[] Languages = ["en", "uk"];
    [Fact]
    public async Task ExistingSettingsOfferTheTourAndDismissalSurvivesDiskRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-tour-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1,\"Language\":\"uk\",\"HidePhysicalPedals\":false}");
            var store = new ApplicationPreferencesStore(path);
            var saved = await store.LoadAsync(CancellationToken.None);
            Assert.False(saved.TourDismissed);
            var model = new PreferencesViewModel();
            model.Load(saved with { TourDismissed = true });
            await store.SaveAsync(model.Snapshot(), CancellationToken.None);
            var restored = await store.LoadAsync(CancellationToken.None);
            Assert.True(restored.TourDismissed);
            Assert.Equal("uk", restored.Language);
            Assert.False(restored.HidePhysicalPedals);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public Task FirstLaunchTakesPrecedenceOverTrayAndDismissalPreservesSettings() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-tour-startup-" + Guid.NewGuid().ToString("N"));
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        desktop.Store.Value = new(StartInTray: true, HidePhysicalPedals: false);
        try
        {
            await using (var main = new MainWindow(new(), desktopServices: desktop.Services, profileLibrary: new(directory)))
            {
                await main.InitializeForLaunchAsync();
                Assert.True(main.ShouldOfferTour);
                Assert.False(main.ShouldStartHidden);
                await main.DismissTourAsync();
                Assert.False(main.ShouldOfferTour);
                Assert.True(main.ShouldStartHidden);
                var settings = Assert.IsType<DashboardViewModel>(main.DataContext).Preferences;
                settings.MinimizeOnClose = true;
                await main.SavePreferencesAsync();
                Assert.True(desktop.Store.Value.TourDismissed);
                Assert.True(desktop.Store.Value.StartInTray);
                Assert.False(desktop.Store.Value.HidePhysicalPedals);
            }
            await using var restarted = new MainWindow(new(), desktopServices: desktop.Services, profileLibrary: new(directory));
            await restarted.InitializeForLaunchAsync();
            Assert.False(restarted.ShouldOfferTour);
            Assert.True(restarted.ShouldStartHidden);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });

    [Fact]
    public Task ConflictsAndRecoveryTakePriorityAndFailedDismissalCanBeRetried() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-tour-blocked-" + Guid.NewGuid().ToString("N"));
        var running = true;
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        try
        {
            await using var main = new MainWindow(new(), desktopServices: desktop.Services with { VendorControlRunning = () => running }, profileLibrary: new(directory));
            await main.InitializeForLaunchAsync();
            Assert.False(main.ShouldOfferTour);
            running = false;
            main.RefreshVendorControlStatus();
            Assert.True(main.ShouldOfferTour);
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            model.Firmware.Blocked = true;
            Assert.False(main.ShouldOfferTour);
            model.Firmware.Blocked = false;
            desktop.Store.Fail = true;
            await main.DismissTourAsync();
            Assert.True(main.ShouldOfferTour);
            Assert.False(model.Preferences.TourDismissed);
            desktop.Store.Fail = false;
            await main.DismissTourAsync();
            Assert.False(main.ShouldOfferTour);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });

    [Theory]
    [InlineData("en", 1340, 940)]
    [InlineData("uk", 1100, 760)]
    public Task TourIsInlineAnchoredAndTranslatedAndNeverStartsHardwareActions(string language, double width, double height) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-inline-tour-" + Guid.NewGuid().ToString("N"));
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        desktop.Store.Value = new(Language: language);
        var connections = 0;
        await using var main = new MainWindow(new(), (_, _, _) =>
        {
            connections++;
            throw new InvalidOperationException("A tour must not open the controller.");
        }, desktop.Services, new(directory))
        { Width = width, Height = height, Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Normal };
        try
        {
            await main.InitializeForLaunchAsync();
            main.Show();
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            var curves = Assert.IsType<Expander>(main.FindName("CurvesSection"));
            curves.IsExpanded = false;
            var original = SherpaAxis.Profiles.ProfileJson.Serialize(model.ValidateEditors().Profile);
            var originallySaved = model.IsProfileSaved;
            await main.ShowGettingStartedAsync();
            var overlay = Assert.IsType<SherpaAxis.App.Controls.TourOverlay>(main.FindName("TourLayer"));
            var next = Assert.IsType<Button>(overlay.FindName("TourNextButton"));
            var back = Assert.IsType<Button>(overlay.FindName("TourBackButton"));
            Assert.True(model.Tour.IsOpen);
            Assert.Empty(main.OwnedWindows);
            model.Tour.MoveBack();
            Assert.Equal(1, model.Tour.Progress);
            for (var index = 0; index < model.Tour.StepCount; index++)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                main.UpdateLayout();
                Assert.Equal(index + 1, model.Tour.Progress);
                Assert.Equal(Localizer.Translate(model.Tour.Current.Title), Assert.IsType<TextBlock>(overlay.FindName("TourTitle")).Text);
                Assert.Equal(model.Tour.Current.Panel, model.PanelMode);
                foreach (var text in new[] { model.Tour.Current.Title, model.Tour.Current.Location, model.Tour.Current.Description, model.Tour.Current.Tip })
                { Assert.NotEqual(text, Localizer.Instance.TranslateText(text, "uk")); }
                Assert.False(overlay.TargetBounds.IsEmpty);
                Assert.True(overlay.CardBounds.Left >= 0 && overlay.CardBounds.Right <= overlay.ActualWidth + 1);
                Assert.True(overlay.CardBounds.Top >= 0 && overlay.CardBounds.Bottom <= overlay.ActualHeight + 1);
                Assert.False(overlay.CardBounds.IntersectsWith(overlay.TargetBounds), $"{model.Tour.Current.Title}: target {overlay.TargetBounds}, card {overlay.CardBounds}, viewport {overlay.RenderSize}");
                if (Environment.GetEnvironmentVariable("SHERPAAXIS_TOUR_PREVIEW") is { Length: > 0 } preview)
                {
                    Directory.CreateDirectory(preview);
                    SavePreview((FrameworkElement)main.Content, Path.Combine(preview, $"interactive-{language}-{index}.png"));
                }
                if (!model.Tour.IsLast) { next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            }
            Assert.Equal(Localizer.Translate("Finish"), next.Content);
            model.Tour.MoveNext();
            Assert.Equal(model.Tour.StepCount, model.Tour.Progress);
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal(model.Tour.StepCount - 1, model.Tour.Progress);
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(model.Tour.IsOpen);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.True(desktop.Store.Value.TourDismissed);
            Assert.False(curves.IsExpanded);
            Assert.Equal(0, connections);
            Assert.Equal(original, SherpaAxis.Profiles.ProfileJson.Serialize(model.ValidateEditors().Profile));
            Assert.Equal(originallySaved, model.IsProfileSaved);
        }
        finally
        {
            await main.DisposeAsync();
            Localizer.Instance.Language = "en";
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    });

    [Fact]
    public Task HighlightPassesInputToRealControlsAndReplayAndSkipRestoreThePreviousPanel() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        desktop.Store.Value = new(TourDismissed: true);
        await using var main = new MainWindow(new(), desktopServices: desktop.Services)
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Normal };
        await main.InitializeForLaunchAsync();
        main.Show();
        await main.RequestPanelModeAsync(SidePanelMode.Settings);
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        Assert.IsType<Button>(main.FindName("GettingStartedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var overlay = Assert.IsType<SherpaAxis.App.Controls.TourOverlay>(main.FindName("TourLayer"));
        var next = Assert.IsType<Button>(overlay.FindName("TourNextButton"));
        Assert.True(model.Tour.IsOpen);
        Assert.True(model.IsTuningMode);
        Assert.Empty(main.OwnedWindows);
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var profile = Assert.IsType<Border>(main.FindName("ProfileBar"));
        profile.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
        Assert.True(model.Tour.Interacted);
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.False(model.Tour.Interacted);
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        main.UpdateLayout();
        Assert.Equal("FilteringPanel", model.Tour.Current.Target);
        var panel = Assert.IsType<StackPanel>(main.FindName("FilteringPanel"));
        var preset = panel.Children.OfType<SherpaAxis.App.Controls.StableComboBox>().First();
        var point = preset.TranslatePoint(new Point(preset.ActualWidth / 2, preset.ActualHeight / 2), main);
        var hit = main.InputHitTest(point) as DependencyObject;
        while (hit is not null && hit != preset) { hit = VisualTreeHelper.GetParent(hit); }
        Assert.Same(preset, hit);
        main.Width = 1100;
        main.Height = 760;
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        main.UpdateLayout();
        Assert.False(overlay.CardBounds.IntersectsWith(overlay.TargetBounds));
        Assert.True(overlay.CardBounds.Right <= overlay.ActualWidth + 1);
        panel.BringIntoView();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        preset.SelectedItem = "Balanced";
        Assert.Equal("Balanced", model.FilterPreset);
        Assert.IsType<Button>(overlay.FindName("TourSkipButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.False(model.Tour.IsOpen);
        Assert.True(model.IsSettingsMode);
        Assert.True(desktop.Store.Value.TourDismissed);
        await main.ShowGettingStartedAsync();
        Assert.Equal(1, model.Tour.Progress);
        Assert.False(model.Tour.Interacted);
        main.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            System.Windows.PresentationSource.FromVisual(main)!, 0, System.Windows.Input.Key.Escape)
        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.False(model.Tour.IsOpen);
    });

    [Fact]
    public Task HelpAndSetupToolsUseEqualFullWidthButtonsInBothLanguages() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var main = new MainWindow(new(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services)
        { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, WindowState = WindowState.Normal };
        await main.InitializeForLaunchAsync();
        main.Show();
        await main.RequestPanelModeAsync(SidePanelMode.Settings);
        var tools = Assert.IsType<Expander>(main.FindName("SetupToolsExpander"));
        tools.IsExpanded = true;
        var guide = Assert.IsType<Button>(main.FindName("SetupGuideButton"));
        var replay = Assert.IsType<Button>(main.FindName("GettingStartedButton"));
        try
        {
            foreach (var language in Languages)
            {
                Localizer.Instance.Language = language;
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                main.UpdateLayout();
                Assert.Equal(replay.ActualWidth, guide.ActualWidth, 1);
                Assert.True(guide.IsVisible);
                Assert.True(guide.ActualWidth > 250);
                foreach (var button in Assert.IsType<StackPanel>(tools.Content).Children.OfType<Button>())
                { Assert.Equal(guide.ActualWidth, button.ActualWidth, 1); }
            }
            if (Environment.GetEnvironmentVariable("SHERPAAXIS_TOUR_PREVIEW") is { Length: > 0 } preview)
            {
                Directory.CreateDirectory(preview);
                SavePreview((FrameworkElement)main.FindName("SettingsSidePanel"), Path.Combine(preview, "settings.png"));
            }
        }
        finally { Localizer.Instance.Language = "en"; }
    });

    private static void SavePreview(FrameworkElement element, string path)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 18, 32)), null, bounds);
            drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
