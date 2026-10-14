using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.App.ViewModels;
using Forms = System.Windows.Forms;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class SettingsAndTrayTests
{
    private static readonly TrayCommand[] ExpectedTrayCommands = [TrayCommand.Show, TrayCommand.Tune, TrayCommand.Calibrate, TrayCommand.Settings, TrayCommand.Exit];
    private static readonly string[] TrayLabels = ["Open SherpaAxis", "Tune", "Calibrate", "Settings", "Exit"];
    [Fact]
    public async Task PreferencesRoundTripAndFailedOrCancelledSavesPreserveTheLastFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var store = new ApplicationPreferencesStore(path);
        try
        {
            Assert.Equal(new(), await store.LoadAsync(CancellationToken.None));
            var preferences = new ApplicationPreferences(MinimizeOnClose: true, StartWithWindows: true, StartInTray: true, MinimizeToTray: true);
            await store.SaveAsync(preferences, CancellationToken.None);
            Assert.Equal(preferences, await store.LoadAsync(CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new(), cancelled.Token));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(SchemaVersion: 5), CancellationToken.None));
            Assert.Equal(preferences, await store.LoadAsync(CancellationToken.None));
            Assert.Single(Directory.EnumerateFiles(directory));
            await File.WriteAllTextAsync(path, "{ invalid");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
            await File.WriteAllTextAsync(path, "{\"SchemaVersion\":2}");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
    }

    [Fact]
    public void StartupCommandQuotesTheExecutableAndRejectsInvalidPaths()
    {
        Assert.Equal("\"C:\\A folder\\SherpaAxis.App.exe\" --startup", WindowsStartupRegistration.BuildCommand(@"C:\A folder\SherpaAxis.App.exe"));
        Assert.Throws<ArgumentException>(() => WindowsStartupRegistration.BuildCommand("SherpaAxis.App.exe"));
        Assert.Throws<ArgumentException>(() => WindowsStartupRegistration.BuildCommand("C:\\bad\"path\\app.exe"));
        Assert.Throws<ArgumentException>(() => WindowsStartupRegistration.BuildCommand("C:\\" + new string('a', 260) + "\\app.exe"));
    }

    [Fact]
    public Task SettingsCheckboxesSaveAndRestartUsesPreferencesAndActualStartupState() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new FakeDesktop();
        await using (var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services))
        {
            await main.InitializeDesktopAsync();
            await main.RequestPanelModeAsync(SidePanelMode.Settings);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var dashboard = Assert.IsType<DashboardViewModel>(main.DataContext);
            Assert.True(dashboard.IsSettingsMode);
            var close = Assert.IsType<CheckBox>(main.FindName("MinimizeOnCloseSetting"));
            close.IsChecked = true;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(desktop.Store.Value.MinimizeOnClose);
            dashboard.Preferences.StartWithWindows = true;
            dashboard.Preferences.StartInTray = true;
            dashboard.Preferences.MinimizeToTray = true;
            await main.SavePreferencesAsync();
            Assert.True(Assert.Single(desktop.Startup.Changes));
            Assert.True(main.ShouldStartHidden);
        }
        desktop.Startup.Enabled = false; // Windows Startup Apps can disable it independently.
        await using var restarted = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services);
        await restarted.InitializeDesktopAsync();
        var settings = Assert.IsType<DashboardViewModel>(restarted.DataContext).Preferences;
        Assert.True(settings.MinimizeOnClose && settings.StartInTray && settings.MinimizeToTray);
        Assert.False(settings.StartWithWindows);
        Assert.Single(desktop.Startup.Changes);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FailedSettingsSaveRollsBackTheCheckboxAndNeverClaimsStartupSucceeded(bool failRegistry) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new FakeDesktop();
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services);
        await main.InitializeDesktopAsync();
        desktop.Store.Fail = !failRegistry;
        desktop.Startup.Fail = failRegistry;
        var settings = Assert.IsType<DashboardViewModel>(main.DataContext).Preferences;
        settings.StartWithWindows = true;
        settings.MinimizeOnClose = true;
        await main.SavePreferencesAsync();
        Assert.False(settings.StartWithWindows || settings.MinimizeOnClose || settings.Saving);
        Assert.Equal(new(), desktop.Store.Value);
        Assert.False(desktop.Startup.Enabled);
        Assert.Contains("not changed", settings.Status, StringComparison.Ordinal);
    });

    [Fact]
    public Task CloseHidesButTrayExitClosesAndReleasesTheTray() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new FakeDesktop();
        desktop.Store.Value = new(MinimizeOnClose: true, StartInTray: true);
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services);
        await main.InitializeDesktopAsync();
        var closed = false;
        main.Closed += (_, _) => closed = true;
        main.Close();
        await Task.Yield();
        Assert.False(closed || main.IsDisposed || desktop.Tray.Disposed);
        var timer = Assert.IsType<DispatcherTimer>(typeof(MainWindow).GetField("refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main));
        Assert.False(timer.IsEnabled);
        desktop.Tray.Command(TrayCommand.Exit);
        for (var attempt = 0; attempt < 100 && !closed; attempt++) { await Task.Delay(5); }
        Assert.True(closed && main.IsDisposed && desktop.Tray.Disposed);
    });

    [Fact]
    public Task RapidPreferenceChangesSaveTheLatestValuesAndExitWaitsForThem() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new FakeDesktop();
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services);
        await main.InitializeDesktopAsync();
        var settings = Assert.IsType<DashboardViewModel>(main.DataContext).Preferences;
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.Store.WriteBarrier = write.Task;
        try
        {
            settings.MinimizeOnClose = true;
            var first = main.SavePreferencesAsync();
            settings.MinimizeOnClose = false;
            settings.StartInTray = true;
            var last = main.SavePreferencesAsync();
            Assert.Same(first, last);
            var exit = main.DisposeAsync().AsTask();
            Assert.False(exit.IsCompleted);
            write.SetResult();
            await exit;
            Assert.False(desktop.Store.Value.MinimizeOnClose);
            Assert.True(desktop.Store.Value.StartInTray);
        }
        finally { write.TrySetResult(); }
    });

    [Fact]
    public Task TrayCommandsUseTheSameModeAndProfileLogic() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new FakeDesktop();
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services)
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Normal };
        await main.InitializeForLaunchAsync();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        var first = model.SelectedProfile!;
        model.Load(DashboardEngine.DefaultProfile());
        desktop.Tray.Profile(first);
        Assert.Same(first, model.SelectedProfile);
        desktop.Tray.Command(TrayCommand.Settings);
        Assert.True(model.IsSettingsMode && main.IsVisible);
        desktop.Tray.Command(TrayCommand.Tune);
        Assert.True(model.IsTuningMode);
        Assert.True(await main.HideToTrayAsync());
        Assert.False(main.IsVisible);
        desktop.Tray.Command(TrayCommand.Show);
        Assert.True(main.IsVisible);
        // Closing a UI test must use the same real exit route, without leaving a hidden native window.
        main.RequestExit();
    });

    [Fact]
    public Task NativeTrayMenuWiresActionsAndChecksTheActiveProfile() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var profile = new ProfileTab { Name = "Balanced" };
        var current = new TrayMenuState([profile], profile, SidePanelMode.Settings, true, true);
        using var tray = new ApplicationTray(() => current);
        tray.RebuildMenu();
        var menu = Assert.IsType<Forms.ContextMenuStrip>(typeof(ApplicationTray).GetField("menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray));
        var commands = new List<TrayCommand>();
        ProfileTab? selected = null;
        tray.CommandRequested += commands.Add;
        tray.ProfileRequested += value => selected = value;
        foreach (var label in TrayLabels)
        { menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == label).PerformClick(); }
        Assert.Equal(ExpectedTrayCommands, commands);
        var profiles = menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == "Switch profile");
        var item = Assert.Single(profiles.DropDownItems.OfType<Forms.ToolStripMenuItem>());
        Assert.True(item.Checked);
        item.PerformClick();
        Assert.Same(profile, selected);
        current = current with { CanSwitchProfile = false, Mode = SidePanelMode.Calibration };
        tray.RebuildMenu();
        Assert.False(menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == "Switch profile").Enabled);
        await Task.CompletedTask;
    });

    internal sealed class FakeDesktop
    {
        internal FakeStore Store { get; } = new();
        internal FakeStartup Startup { get; } = new();
        internal FakeTray Tray { get; } = new();
        internal DesktopServices Services => new(Store, Startup, _ => Tray);
    }
    internal sealed class FakeStore : IApplicationPreferencesStore
    {
        internal ApplicationPreferences Value { get; set; } = new();
        internal bool Fail { get; set; }
        internal Task? WriteBarrier { get; set; }
        public Task<ApplicationPreferences> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
        public async Task SaveAsync(ApplicationPreferences preferences, CancellationToken cancellationToken)
        {
            if (Fail) { throw new IOException("Test settings disk failure"); }
            if (WriteBarrier is { } pending) { await pending.WaitAsync(cancellationToken); }
            Value = preferences;
        }
    }
    internal sealed class FakeStartup : IStartupRegistration
    {
        internal bool Enabled { get; set; }
        internal bool Fail { get; set; }
        internal List<bool> Changes { get; } = [];
        public bool IsEnabled => Enabled;
        public void SetEnabled(bool enabled)
        { if (Fail) { throw new UnauthorizedAccessException("Test startup permission failure"); } Enabled = enabled; Changes.Add(enabled); }
    }
    internal sealed class FakeTray : IApplicationTray
    {
        internal bool Disposed { get; private set; }
        public event Action<TrayCommand>? CommandRequested;
        public event Action<ProfileTab>? ProfileRequested;
        internal void Command(TrayCommand command) => CommandRequested?.Invoke(command);
        internal void Profile(ProfileTab profile) => ProfileRequested?.Invoke(profile);
        public void Dispose() { Disposed = true; }
    }
}
