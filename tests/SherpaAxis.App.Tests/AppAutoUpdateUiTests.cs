using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.Services.Updates;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class AppAutoUpdateUiTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-auto-update-ui-" + Guid.NewGuid().ToString("N"));
    private AppUpdateInstallation Installation() => new(Path.Combine(directory, "app"), AppUpdateKind.Zip, new Version(1, 5, 0));
    private PreparedAppUpdate Package(AppRelease release) => new(release, Installation(), directory, Path.Combine(directory, "package.zip"), new string('0', 64));

    [Fact]
    public Task ClickingAgainCancelsDownloadWithoutClosingTheAppOrStartingAnInstaller() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(new(), desktopServices: desktop.Services with
        {
            AppUpdates = new(Installation, async (release, _, progress, token) =>
            {
                progress.Report(new("Downloading update…", 42)); downloading.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Package(release);
            }, (_, _, _) => { starts++; return Task.FromResult<IAppUpdateHandoff>(new Handoff()); })
        }, profileLibrary: new(Path.Combine(directory, "profiles")))
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Normal };
        await main.InitializeForLaunchAsync(); main.Show();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        model.Updates.Apply(new("v1.6.0"));
        var first = main.InstallAvailableAppUpdateAsync();
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.True(model.CanTune && main.IsVisible && model.Updates.IsUpdating);
        Assert.Contains("42%", model.Updates.LinkLabel, StringComparison.Ordinal);
        UiRefinementTests.CapturePreview(Assert.IsType<DockPanel>(main.FindName("AppHeader")), "auto-update-downloading.png");
        Assert.True(model.CanUpdateApplication);
        Assert.Same(first, main.InstallAvailableAppUpdateAsync());
        await first;
        Assert.False(main.IsDisposed || model.Updates.IsUpdating);
        Assert.True(model.CanTune && main.IsVisible);
        Assert.Equal(0, starts);
        Assert.Equal("Update download cancelled.", model.Updates.Status);
    });

    [Theory]
    [InlineData("calibration")]
    [InlineData("firmware")]
    [InlineData("recovery")]
    public Task HardwareOperationsBlockUpdatingBeforeAnyFilesAreDownloaded(string state) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var downloads = 0;
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(new(), desktopServices: desktop.Services with
        {
            AppUpdates = new(Installation, (release, _, _, _) => { downloads++; return Task.FromResult(Package(release)); },
                (_, _, _) => Task.FromResult<IAppUpdateHandoff>(new Handoff()))
        }, profileLibrary: new(Path.Combine(directory, "profiles")));
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        model.Updates.Apply(new("v1.6.0"));
        if (state == "calibration") { model.IsCalibrationMode = true; }
        else if (state == "firmware") { model.Firmware.Running = true; }
        else { model.Recovery.Fail("Pending recovery"); }
        Assert.False(model.CanUpdateApplication);
        await main.InstallAvailableAppUpdateAsync();
        Assert.Equal(0, downloads);
        Assert.False(main.IsDisposed);
        model.IsCalibrationMode = false; model.Firmware.Running = false; model.Recovery.Apply([]);
    });

    [Fact]
    public Task ExitingDuringDownloadWaitsForCancellationAndNeverStartsTheInstaller() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var starts = 0;
        await using var main = new MainWindow(new(), desktopServices: desktop.Services with
        {
            AppUpdates = new(Installation, async (release, _, _, token) =>
            {
                downloading.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cleaned = true; }
                return Package(release);
            }, (_, _, _) => { starts++; return Task.FromResult<IAppUpdateHandoff>(new Handoff()); })
        }, profileLibrary: new(Path.Combine(directory, "profiles")));
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        model.Updates.Apply(new("v1.6.0"));
        var operation = main.InstallAvailableAppUpdateAsync();
        await downloading.Task;
        await main.DisposeAsync();
        await operation;
        Assert.True(cleaned && main.IsDisposed);
        Assert.Equal(0, starts);
    });

    [Fact]
    public Task SuccessfulUpdateBacksUpUnsavedDraftsAndClosesCleanlyBeforeCommittingTheHandoff() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        var draftPath = Path.Combine(directory, "drafts.json");
        var handoff = new Handoff();
        MainWindow? window = null;
        await using var main = new MainWindow(new(), desktopServices: desktop.Services with
        {
            Drafts = new ProfileDraftStore(draftPath),
            AppUpdates = new(Installation, (release, _, _, _) => Task.FromResult(Package(release)), (_, _, _) =>
            {
                Assert.True(File.Exists(draftPath));
                Assert.False(window!.IsDisposed);
                return Task.FromResult<IAppUpdateHandoff>(handoff);
            })
        }, profileLibrary: new(Path.Combine(directory, "profiles")))
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Normal };
        window = main;
        await main.InitializeForLaunchAsync(); main.Show();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        model.Editor.LowerDeadzone = "7"; model.RefreshUnsavedState();
        model.Updates.Apply(new("v1.6.0"));
        handoff.OnCommit = () => Assert.True(main.IsDisposed);
        await main.InstallAvailableAppUpdateAsync();
        Assert.True(main.IsDisposed && handoff.Committed && handoff.Disposed);
        Assert.Contains("7", await File.ReadAllTextAsync(draftPath), StringComparison.Ordinal);
    });

    [Fact]
    public Task FailedDraftBackupNeverStartsTheHandoffAndKeepsTheAppEditable() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        var draftPath = Path.Combine(directory, "blocked-drafts.json");
        var starts = 0;
        await using var main = new MainWindow(new(), desktopServices: desktop.Services with
        {
            Drafts = new ProfileDraftStore(draftPath),
            AppUpdates = new(Installation, (release, _, _, _) => Task.FromResult(Package(release)), (_, _, _) =>
            { starts++; return Task.FromResult<IAppUpdateHandoff>(new Handoff()); })
        }, profileLibrary: new(Path.Combine(directory, "profiles")));
        await main.InitializeForLaunchAsync();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        model.Editor.LowerDeadzone = "7"; model.RefreshUnsavedState();
        Directory.CreateDirectory(draftPath);
        try
        {
            model.Updates.Apply(new("v1.6.0"));
            await main.InstallAvailableAppUpdateAsync();
            Assert.Equal(0, starts);
            Assert.False(main.IsDisposed || model.Updates.IsUpdating);
            Assert.True(model.CanTune);
            Assert.Contains("Could not update", model.Updates.Status, StringComparison.Ordinal);
        }
        finally { Directory.Delete(draftPath); }
    });

    [Fact]
    public Task PreparingInstallationBlocksOrdinaryClosingAndFailureRestoresEditing() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        var preparing = new TaskCompletionSource<IAppUpdateHandoff>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var main = new MainWindow(new(), desktopServices: desktop.Services with
        {
            AppUpdates = new(Installation, (release, _, _, _) => Task.FromResult(Package(release)), (_, _, _) => preparing.Task)
        }, profileLibrary: new(Path.Combine(directory, "profiles")))
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Normal };
        await main.InitializeForLaunchAsync(); main.Show();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        model.Updates.Apply(new("v1.6.0"));
        var updating = main.InstallAvailableAppUpdateAsync();
        Assert.True(model.Updates.IsInstalling);
        main.UpdateLayout();
        UiRefinementTests.CapturePreview(Assert.IsType<DockPanel>(main.FindName("AppHeader")), "auto-update-preparing.png");
        Assert.False(model.CanTune || model.CanUpdateApplication);
        main.Close();
        Assert.True(main.IsVisible);
        Assert.False(main.IsDisposed);
        preparing.SetException(new IOException("Helper could not prepare update"));
        await updating;
        Assert.True(model.CanTune && main.IsVisible);
        Assert.False(model.Updates.IsUpdating || model.Updates.IsInstalling || main.IsDisposed);
    });

    private sealed class Handoff : IAppUpdateHandoff
    {
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public Action? OnCommit { get; set; }
        public void Commit() { OnCommit?.Invoke(); Committed = true; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
}
