using System.IO;
using System.Windows;
using System.Windows.Controls;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class OverlayPlacementTests
{
    [Fact]
    public void PlacementKeepsSecondaryScreensAndRecoversFromUnpluggedScreens()
    {
        Rect[] screens = [new(0, 0, 1920, 1040), new(-1920, 0, 1920, 1080)];
        Assert.Equal(new Point(-1500, 200), OverlayPlacement.Constrain(new(-1500, 200), new(640, 140), screens));
        Assert.Equal(new Point(0, 200), OverlayPlacement.Constrain(new(-1500, 200), new(640, 140), screens.Take(1).ToArray()));
        Assert.Equal(new Point(1280, 900), OverlayPlacement.Constrain(new(5000, 3000), new(640, 140), screens));
        Assert.Equal(new Point(40, 0), OverlayPlacement.Constrain(new(0, 0), new(640, 140), [new(40, 0, 1880, 1040)]));
    }

    [Fact]
    public void MissingOrInvalidPositionDoesNotBreakExistingPreferences()
    {
        var settings = new PedalOverlaySettingsViewModel();
        settings.Load(new(Left: double.NaN, Top: 50)); Assert.Null(settings.Left); Assert.Null(settings.Top);
        settings.Load(new(Left: 50)); Assert.Null(settings.Left); Assert.Null(settings.Top);
        settings.Load(new(Left: double.MaxValue, Top: 100)); Assert.Null(settings.Left); Assert.Null(settings.Top);
        settings.Load(new(Left: -1500, Top: -500)); Assert.Equal(-1500, settings.Left); Assert.Equal(-500, settings.Top);
        settings.Load(null); Assert.Null(settings.Snapshot());
    }

    [Fact]
    public Task PositionAndSettingsSurviveClosingAndRestartWhileVisibilityRemainsExplicit() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-overlay-position-" + Guid.NewGuid().ToString("N"));
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        Point position;
        try
        {
            await using (var main = new MainWindow(new(), desktopServices: desktop.Services, profileLibrary: new(folder)))
            {
                await main.InitializeForLaunchAsync();
                await main.RequestPanelModeAsync(SidePanelMode.PedalOverlay);
                var checkbox = Assert.IsType<CheckBox>(main.FindName("OverlayEnabledCheck"));
                checkbox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                var view = main.OpenCompactView();
                var settings = ((CompactPedalViewModel)view.DataContext).Settings;
                settings.Width = 700; settings.Height = 180; settings.WindowSeconds = 23;
                settings.ShowRaw = true; settings.ShowFiltered = true;
                view.Left = 120; view.Top = 130; view.UpdateLayout();
                position = view.PointToScreen(new Point());
                view.Close(); Assert.False(checkbox.IsChecked);
                var reopened = main.OpenCompactView();
                Assert.Equal(position, reopened.PointToScreen(new Point()));
                Assert.Equal(700, reopened.Width); Assert.Equal(180, reopened.Height);
                // Exit must flush the most recent drag even before the save debounce expires.
                reopened.Left = 230; reopened.Top = 240;
                position = reopened.PointToScreen(new Point());
            }
            Assert.Equal(position.X, desktop.Store.Value.Overlay!.Left); Assert.Equal(position.Y, desktop.Store.Value.Overlay.Top);
            await using var restarted = new MainWindow(new(), desktopServices: desktop.Services, profileLibrary: new(folder));
            await restarted.InitializeForLaunchAsync();
            var saved = ((DashboardViewModel)restarted.DataContext).Preferences.Overlay;
            Assert.False(saved.IsVisible);
            var restored = restarted.OpenCompactView();
            Assert.Equal(position, restored.PointToScreen(new Point()));
            Assert.Equal(700, restored.Width); Assert.Equal(180, restored.Height);
            Assert.Equal(23, saved.WindowSeconds); Assert.True(saved.ShowRaw && saved.ShowFiltered);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });

    [Fact]
    public Task AFailedPositionSaveKeepsTheLastSavedPlacementAndCanBeRetried() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-overlay-position-failure-" + Guid.NewGuid().ToString("N"));
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        var writes = new CountingPreferences(desktop.Store);
        try
        {
            await using var main = new MainWindow(new(), desktopServices: desktop.Services with { Preferences = writes }, profileLibrary: new(folder));
            await main.InitializeForLaunchAsync();
            var view = main.OpenCompactView(); view.Left = 150; view.Top = 160;
            await main.SavePreferencesAsync(); var original = view.PointToScreen(new Point());
            desktop.Store.Fail = true; view.Left = 250; view.Top = 260;
            await main.SavePreferencesAsync();
            Assert.Equal(original, view.PointToScreen(new Point()));
            Assert.Equal(original.X, desktop.Store.Value.Overlay!.Left);
            var failedWrites = writes.Saves;
            await Task.Delay(650);
            Assert.Equal(failedWrites, writes.Saves); // Restoring preferences must not schedule another failed save.
            desktop.Store.Fail = false; view.Left = 250; view.Top = 260;
            await main.SavePreferencesAsync();
            Assert.Equal(view.PointToScreen(new Point()).X, desktop.Store.Value.Overlay!.Left);
        }
        finally { desktop.Store.Fail = false; if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });

    private sealed class CountingPreferences(IApplicationPreferencesStore inner) : IApplicationPreferencesStore
    {
        internal int Saves { get; private set; }
        public Task<ApplicationPreferences> LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);
        public Task SaveAsync(ApplicationPreferences preferences, CancellationToken cancellationToken)
        { Saves++; return inner.SaveAsync(preferences, cancellationToken); }
    }
}
