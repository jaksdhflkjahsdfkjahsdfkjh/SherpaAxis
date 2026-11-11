using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class PedalOverlaySettingsTests
{
    [Fact]
    public void DefaultsAndInvalidSavedNumbersLoadSafely()
    {
        var settings = new PedalOverlaySettingsViewModel();
        Assert.Null(settings.Snapshot()); Assert.False(settings.IsVisible);
        settings.Load(new(double.NaN, true, true, false, -1, 10000));
        Assert.Equal(10, settings.WindowSeconds); Assert.Equal(640, settings.Width); Assert.Equal(140, settings.Height);
        Assert.True(settings.ShowRaw && settings.ShowFiltered); Assert.False(settings.ShowOutput);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.WindowSeconds = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Width = double.PositiveInfinity);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Height = 10);
    }

    [Fact]
    public void MouseActivationIsRejectedForTheOverlay()
    {
        var handled = false;
        Assert.Equal(3, CompactPedalWindow.PreventMouseActivation(0, 0x21, 0, 0, ref handled)); Assert.True(handled);
        handled = false;
        Assert.Equal(0, CompactPedalWindow.PreventMouseActivation(0, 0x20, 0, 0, ref handled)); Assert.False(handled);
    }

    [Fact]
    public Task SettingsPersistButTheOverlayAndPauseDoNotAutomaticallyStart() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        var folder = TempFolder();
        try
        {
            await using (var main = new MainWindow(new(), desktopServices: desktop.Services, profileLibrary: new(folder)))
            {
                await main.InitializeForLaunchAsync();
                var model = (DashboardViewModel)main.DataContext;
                model.Preferences.Overlay.WindowSeconds = 25; model.Preferences.Overlay.Width = 680; model.Preferences.Overlay.Height = 180;
                model.Preferences.Overlay.ShowRaw = model.Preferences.Overlay.ShowFiltered = true;
                model.Preferences.Overlay.GraphPaused = true;
                main.OpenCompactView();
                await main.SavePreferencesAsync();
                Assert.NotNull(desktop.Store.Value.Overlay);
            }
            await using var restarted = new MainWindow(new(), desktopServices: desktop.Services, profileLibrary: new(folder));
            await restarted.InitializeForLaunchAsync();
            var settings = ((DashboardViewModel)restarted.DataContext).Preferences.Overlay;
            Assert.Equal(25, settings.WindowSeconds); Assert.Equal(680, settings.Width); Assert.Equal(180, settings.Height);
            Assert.True(settings.ShowRaw && settings.ShowFiltered);
            Assert.False(settings.IsVisible); Assert.False(settings.GraphPaused);
            var view = restarted.OpenCompactView();
            Assert.Equal(680, view.Width); Assert.Equal(180, view.Height);
        }
        finally { Directory.Delete(folder, true); }
    });

    [Fact]
    public Task ExitFlushesTheLastOverlayChangeBeforeTheDebounceFires() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new SettingsAndTrayTests.FakeDesktop(); var folder = TempFolder();
        try
        {
            await using var main = new MainWindow(new(), desktopServices: desktop.Services, profileLibrary: new(folder));
            await main.InitializeForLaunchAsync();
            ((DashboardViewModel)main.DataContext).Preferences.Overlay.WindowSeconds = 42;
            await main.DisposeAsync();
            Assert.Equal(42, desktop.Store.Value.Overlay!.WindowSeconds);
        }
        finally { Directory.Delete(folder, true); }
    });

    [Fact]
    public Task AFailedSaveRestoresThePreviousSettingsAndTheLiveOverlaySize() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new SettingsAndTrayTests.FakeDesktop(); var folder = TempFolder();
        try
        {
            await using var main = new MainWindow(new(), desktopServices: desktop.Services, profileLibrary: new(folder));
            await main.InitializeForLaunchAsync();
            var settings = ((DashboardViewModel)main.DataContext).Preferences.Overlay;
            var view = main.OpenCompactView();
            settings.Width = 750; settings.WindowSeconds = 30;
            desktop.Store.Fail = true;
            await main.SavePreferencesAsync();
            Assert.Equal(640, settings.Width); Assert.Equal(640, view.Width); Assert.Equal(10, settings.WindowSeconds);
            Assert.True(settings.IsVisible);
            desktop.Store.Fail = false;
        }
        finally { Directory.Delete(folder, true); }
    });

    [Fact]
    public Task ChangingOverlaySettingsDoesNotReconfigureHidHide() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var hide = new InputVisibilityTests.FakeHide();
        var desktop = new SettingsAndTrayTests.FakeDesktop(); var folder = TempFolder();
        try
        {
            await using var main = new MainWindow(new(), desktopServices: desktop.Services with { InputVisibility = hide.Service }, profileLibrary: new(folder));
            await main.InitializeForLaunchAsync();
            var calls = hide.Calls;
            ((DashboardViewModel)main.DataContext).Preferences.Overlay.WindowSeconds = 15;
            await main.SavePreferencesAsync();
            Assert.Equal(calls, hide.Calls);
        }
        finally { Directory.Delete(folder, true); }
    });

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task OverlayTabControlsTheDisplayAndCheckboxTracksVisibility(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new SettingsAndTrayTests.FakeDesktop(); var folder = TempFolder();
        try
        {
            Localizer.Instance.Language = language;
            var engine = new DashboardEngine();
            await using var main = new MainWindow(engine, desktopServices: desktop.Services, profileLibrary: new(folder));
            await main.InitializeForLaunchAsync(); Localizer.Instance.Language = language;
            await main.RequestPanelModeAsync(SidePanelMode.PedalOverlay);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var enabled = Assert.IsType<CheckBox>(main.FindName("OverlayEnabledCheck"));
            var settings = ((DashboardViewModel)main.DataContext).Preferences.Overlay;
            Assert.False(enabled.IsChecked);
            enabled.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.True(settings.IsVisible); Assert.True(enabled.IsChecked);
            var view = main.OpenCompactView();
            Assert.Equal(WindowStyle.None, view.WindowStyle); Assert.False(view.ShowActivated);
            Assert.Empty(WorkspaceSpacingTests.Descendants(view).OfType<System.Windows.Controls.Primitives.ButtonBase>());
            Assert.Empty(WorkspaceSpacingTests.Descendants(view).OfType<Slider>());
            var slider = Assert.IsType<Slider>(main.FindName("OverlayWindowSlider"));
            slider.Value = 12;
            Assert.Equal(12, settings.WindowSeconds);
            var raw = Assert.IsType<CheckBox>(main.FindName("OverlayRawCheck")); raw.IsChecked = true;
            settings.WindowSeconds = 5; settings.ShowFiltered = true;
            view.UpdateLayout();
            Assert.True(settings.ShowRaw);
            var group = Assert.IsType<GroupBox>(main.FindName("OverlaySettingsGroup"));
            group.Measure(new Size(390, double.PositiveInfinity)); group.Arrange(new Rect(0, 0, 390, group.DesiredSize.Height)); group.UpdateLayout();
            Assert.Equal(Localizer.Translate("Graph"), group.Header);
            UiRefinementTests.CapturePreview(group, "overlay-settings-" + language + ".png");
            enabled.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Assert.False(settings.IsVisible); Assert.False(enabled.IsChecked);
            main.OpenCompactView(); Assert.True(enabled.IsChecked);
            main.OpenCompactView().Close(); Assert.False(enabled.IsChecked);
        }
        finally { Localizer.Instance.Language = "en"; Directory.Delete(folder, true); }
    });

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task TrayCheckboxTracksTheOverlayWithoutRestoringTheMainWindow(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new SettingsAndTrayTests.FakeDesktop(); var folder = TempFolder();
        ApplicationTray? tray = null;
        try
        {
            await using var main = new MainWindow(new(), desktopServices: desktop.Services with
            { CreateTray = state => tray = new ApplicationTray(state) }, profileLibrary: new(folder));
            await main.InitializeForLaunchAsync();
            Localizer.Instance.Language = language;
            Assert.True(await main.HideToTrayAsync());
            Assert.NotNull(tray);
            var menu = Assert.IsType<System.Windows.Forms.ContextMenuStrip>(typeof(ApplicationTray)
                .GetField("menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tray));
            System.Windows.Forms.ToolStripMenuItem Checkbox()
            {
                tray.RebuildMenu();
                return menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(item => item.Text == Localizer.Translate("Pedal overlay"));
            }
            Assert.True(menu.ShowCheckMargin);
            var checkbox = Checkbox(); Assert.False(checkbox.Checked);
            checkbox.PerformClick(); Assert.True(checkbox.Checked);
            Assert.True(main.GetTrayState().OverlayVisible); Assert.False(main.IsVisible);
            var view = main.OpenCompactView();
            Assert.True(view.IsVisible); Assert.False(view.ShowActivated);
            checkbox = Checkbox(); Assert.True(checkbox.Checked);
            checkbox.PerformClick(); Assert.False(checkbox.Checked);
            Assert.False(main.GetTrayState().OverlayVisible); Assert.False(main.IsVisible);
            Assert.False(view.IsVisible);
            Assert.IsType<CheckBox>(main.FindName("OverlayEnabledCheck")).RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Assert.True(Checkbox().Checked);
            main.OpenCompactView().Close();
            Assert.False(Checkbox().Checked);
            Assert.False(main.IsVisible);
        }
        finally { Localizer.Instance.Language = "en"; Directory.Delete(folder, true); }
    });

    private static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "SherpaAxis-overlay-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
}
