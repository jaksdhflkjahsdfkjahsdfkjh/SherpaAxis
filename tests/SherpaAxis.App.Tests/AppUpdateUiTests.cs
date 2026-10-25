using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Updates;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class AppUpdateUiTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-update-ui-" + Guid.NewGuid().ToString("N"));
    private AppUpdateChecker Checker(HttpClient client) => new(client, Path.Combine(directory, "updates.json"));
    private static string NewerRelease(int offset = 1)
    {
        var current = typeof(MainWindow).Assembly.GetName().Version!;
        return FormattableString.Invariant($"v{current.Major}.{current.Minor + offset}.0");
    }

    [Fact]
    public Task StartupDoesNotWaitForUpdatesAndShutdownCancelsTheCheck() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AppUpdateCheckerTests.StubHandler(async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped.SetResult(); }
            return AppUpdateCheckerTests.Json(AppUpdateCheckerTests.ReleaseJson("v2.0"));
        });
        using var client = new HttpClient(handler);
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services with { Updates = Checker(client) });
        await main.InitializeForLaunchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        Assert.True(model.Updates.Busy);
        Assert.True(model.CanEdit && model.CanTune);
        Assert.False(model.Recovery.NeedsAttention || model.Firmware.Blocked);
        var first = main.CheckForAppUpdatesAsync();
        Assert.Same(first, main.CheckForAppUpdatesAsync());
        Assert.False(first.IsCompleted);
        Assert.Equal(1, handler.Requests);
        await main.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(main.IsDisposed);
        Assert.Empty(desktop.Tray.UpdateNotifications);
    });

    [Fact]
    public Task AutoOptOutIsSavedAndManualCheckStillWorksWithOneHiddenTrayNotice() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var handler = new AppUpdateCheckerTests.StubHandler((_, _) => Task.FromResult(AppUpdateCheckerTests.Json(AppUpdateCheckerTests.ReleaseJson(NewerRelease()))));
        using var client = new HttpClient(handler);
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        desktop.Store.Value = new(CheckForUpdates: false, TourDismissed: true);
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services with { Updates = Checker(client) });
        await main.InitializeForLaunchAsync();
        Assert.Equal(0, handler.Requests);
        await main.CheckForAppUpdatesAsync(manual: false);
        Assert.Equal(0, handler.Requests);
        await main.CheckForAppUpdatesAsync();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        Assert.True(model.Updates.HasUpdate);
        Assert.Equal(NewerRelease(), Assert.Single(desktop.Tray.UpdateNotifications));
        Assert.False(main.IsVisible);
        await main.CheckForAppUpdatesAsync();
        Assert.Single(desktop.Tray.UpdateNotifications);
        Assert.Equal(2, handler.Requests);
        var check = Assert.IsType<CheckBox>(main.FindName("CheckForUpdatesSetting"));
        check.IsChecked = true;
        await main.SavePreferencesAsync();
        Assert.True(desktop.Store.Value.CheckForUpdates);
        Assert.Equal(2, handler.Requests);
        check.IsChecked = false;
        await main.SavePreferencesAsync();
        Assert.False(desktop.Store.Value.CheckForUpdates);
    });

    [Fact]
    public Task AutomaticCheckRunsOnlyAtStartupAndFetchesFreshMetadataOnTheNextLaunch() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tag = NewerRelease();
        using var handler = new AppUpdateCheckerTests.StubHandler(async (_, token) =>
        {
            await ready.Task.WaitAsync(token);
            return AppUpdateCheckerTests.Json(AppUpdateCheckerTests.ReleaseJson(tag));
        });
        using var client = new HttpClient(handler);
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        desktop.Store.Value = new(TourDismissed: true);
        await using (var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services with { Updates = Checker(client) }))
        {
            await main.InitializeForLaunchAsync();
            var startup = main.CheckForAppUpdatesAsync();
            ready.SetResult();
            await startup;
            Assert.Equal(1, handler.Requests);
            await main.InitializeForLaunchAsync();
            await main.RequestPanelModeAsync(SidePanelMode.Settings);
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            model.Preferences.CheckForUpdates = false;
            await main.SavePreferencesAsync();
            model.Preferences.CheckForUpdates = true;
            await main.SavePreferencesAsync();
            await main.RequestPanelModeAsync(SidePanelMode.Tuning);
            Assert.Equal(1, handler.Requests);
            Assert.Equal(NewerRelease(), model.Updates.AvailableVersion);
        }
        tag = NewerRelease(2);
        await using var restarted = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services with { Updates = Checker(client) });
        await restarted.InitializeForLaunchAsync();
        var dashboard = Assert.IsType<DashboardViewModel>(restarted.DataContext);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (dashboard.Updates.Busy && DateTime.UtcNow < deadline) { await Task.Delay(1); }
        Assert.False(dashboard.Updates.Busy);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(NewerRelease(2), dashboard.Updates.AvailableVersion);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NetworkAndMalformedResponseErrorsStayInUpdateStatus(bool malformed) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var handler = new AppUpdateCheckerTests.StubHandler((_, _) => malformed
            ? Task.FromResult(AppUpdateCheckerTests.Json("not JSON"))
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("Network unavailable")));
        using var client = new HttpClient(handler);
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services with { Updates = Checker(client) });
        await main.InitializeDesktopAsync();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        var message = model.Message;
        await main.CheckForAppUpdatesAsync();
        Assert.Contains("Could not check", model.Updates.Status, StringComparison.Ordinal);
        Assert.True(model.Updates.CanCheck && model.CanEdit && model.CanTune);
        Assert.False(model.Updates.HasUpdate || model.Recovery.NeedsAttention || model.Firmware.Blocked);
        Assert.Equal(message, model.Message);
        Assert.Empty(desktop.Tray.UpdateNotifications);
    });

    [Fact]
    public Task AvailableReleaseIsLocalizedInSettingsAndFooterWithoutChangingTheirHeight() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var handler = new AppUpdateCheckerTests.StubHandler((_, _) => Task.FromResult(AppUpdateCheckerTests.Json(AppUpdateCheckerTests.ReleaseJson(NewerRelease()))));
        using var client = new HttpClient(handler);
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        desktop.Store.Value = new(CheckForUpdates: false, TourDismissed: true);
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services with { Updates = Checker(client) })
        { ShowInTaskbar = false, Opacity = 0, Width = 1200, Height = 850, WindowState = WindowState.Normal };
        await main.InitializeForLaunchAsync();
        await main.RequestPanelModeAsync(SidePanelMode.Settings);
        var footer = Assert.IsType<Border>(main.FindName("FooterBar"));
        var panel = Assert.IsType<StackPanel>(main.FindName("AppUpdatesPanel"));
        var viewRelease = Assert.IsType<Button>(main.FindName("ViewAppReleaseButton"));
        main.Show(); main.UpdateLayout();
        var height = footer.ActualHeight;
        Assert.Equal(Visibility.Collapsed, viewRelease.Visibility);
        await main.CheckForAppUpdatesAsync();
        Assert.Empty(desktop.Tray.UpdateNotifications);
        var link = Assert.IsType<Hyperlink>(main.FindName("AppUpdateLink"));
        Assert.Equal($"https://github.com/{ApplicationLinks.Repository}/releases/tag/{NewerRelease()}", link.NavigateUri.AbsoluteUri);
        try
        {
            Localizer.Instance.Language = "en";
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            main.UpdateLayout();
            Assert.Equal(Visibility.Visible, viewRelease.Visibility);
            Assert.Equal(height, footer.ActualHeight);
            Assert.Equal($"Update available: {NewerRelease()}", Assert.IsType<Run>(link.Inlines.FirstInline).Text);
            Assert.Equal($"A newer version is available: {NewerRelease()}.", Assert.IsType<TextBlock>(main.FindName("AppUpdateStatus")).Text);
            UiRefinementTests.CapturePreview(footer, "update-footer-en.png");
            UiRefinementTests.CapturePreview(panel, "update-settings-en.png");
            Localizer.Instance.Language = "uk";
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            main.UpdateLayout();
            Assert.Equal($"Доступне оновлення: {NewerRelease()}", Assert.IsType<Run>(link.Inlines.FirstInline).Text);
            Assert.Equal($"Доступна новіша версія: {NewerRelease()}.", Assert.IsType<TextBlock>(main.FindName("AppUpdateStatus")).Text);
            UiRefinementTests.CapturePreview(footer, "update-footer-uk.png");
            UiRefinementTests.CapturePreview(panel, "update-settings-uk.png");
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            model.Updates.Apply(new("v1.1.0"));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(model.Updates.HasUpdate);
            Assert.Equal(Visibility.Collapsed, viewRelease.Visibility);
            Assert.Equal("Встановлено актуальну версію (v1.2.0).", Assert.IsType<TextBlock>(main.FindName("AppUpdateStatus")).Text);
        }
        finally { Localizer.Instance.Language = "en"; }
    });

    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
}
