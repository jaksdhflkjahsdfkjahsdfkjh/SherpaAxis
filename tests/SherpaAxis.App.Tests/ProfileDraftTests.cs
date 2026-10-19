using System.IO;
using System.Windows;
using System.Windows.Controls;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class ProfileDraftTests
{
    [Fact]
    public void RecoveryPreservesNewEditsToTheSameSavedProfileEvenBeforeTheNextRefresh()
    {
        var model = new DashboardViewModel(new DashboardEngine());
        var path = Path.Combine(Path.GetTempPath(), "Profiles", "saved.json");
        var saved = model.AddSavedProfile(new("Race", DashboardEngine.DefaultProfile().Axes), path);
        model.SwitchProfile(saved);
        model.Editor.LowerDeadzone = "7";
        var previous = new DashboardViewModel(new DashboardEngine());
        previous.Editor.LowerDeadzone = "12";
        var draft = Assert.Single(previous.CaptureDrafts().Profiles) with { SavedFileName = "saved.json" };
        model.RestoreDrafts(new(1, [draft]), Path.GetDirectoryName(path)!);
        Assert.Contains(saved, model.ProfileTabs);
        Assert.Equal("7", saved.Editors[PedalAxis.Brake].LowerDeadzone);
        Assert.Equal("12", model.Editor.LowerDeadzone);
        Assert.Null(model.SelectedProfile!.FilePath);
        Assert.True(model.SelectedProfile.RequiresApply);
    }
    [Fact]
    public Task DraftBackupRunsWhileHiddenAndExitFlushesTheLatestKeystroke() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = TempFolder();
        try
        {
            var store = new ProfileDraftStore(Path.Combine(folder, "session.json"));
            await using (var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services with { Drafts = store }, profileLibrary: new(Path.Combine(folder, "Profiles"))))
            {
                await main.InitializeForLaunchAsync();
                Assert.False(main.IsVisible);
                ((DashboardViewModel)main.DataContext).Editor.LowerDeadzone = "13";
                var deadline = Environment.TickCount64 + 6000;
                while ((!File.Exists(store.FilePath) || (await store.LoadAsync())?.Profiles.Count != 1) && Environment.TickCount64 < deadline) { await Task.Delay(25); }
                Assert.Equal("13", Assert.Single((await store.LoadAsync())!.Profiles).Editors[PedalAxis.Brake].LowerDeadzone);
                ((DashboardViewModel)main.DataContext).Editor.UpperDeadzone = "last keystroke";
            }
            Assert.Equal("last keystroke", Assert.Single((await store.LoadAsync())!.Profiles).Editors[PedalAxis.Brake].UpperDeadzone);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });

    [Fact]
    public async Task CancelledDraftWritePreservesThePreviousRecoveryFile()
    {
        var folder = TempFolder();
        try
        {
            var store = new ProfileDraftStore(Path.Combine(folder, "session.json"));
            var model = new DashboardViewModel(new DashboardEngine()); model.Editor.Minimum = "original draft";
            await store.SaveAsync(model.CaptureDrafts());
            var original = await File.ReadAllBytesAsync(store.FilePath);
            model.Editor.Minimum = "replacement";
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(model.CaptureDrafts(), cancel.Token));
            Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath));
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }
    [Fact]
    public async Task InvalidEditorTextRoundTripsWithoutBecomingAValidSavedProfile()
    {
        var folder = TempFolder();
        try
        {
            var store = new ProfileDraftStore(Path.Combine(folder, "draft.json"));
            var model = new DashboardViewModel(new DashboardEngine());
            model.Editor.FilterMilliseconds = "unfinished filter";
            model.Editor.LowerDeadzone = "-";
            model.SetCurvePreset(PedalAxis.Throttle, "Progressive");
            var draft = model.CaptureDrafts();
            await store.SaveAsync(draft);
            var recovered = Assert.Single((await store.LoadAsync())!.Profiles);
            Assert.Equal("unfinished filter", recovered.Editors[PedalAxis.Brake].FilterMilliseconds);
            Assert.Equal("-", recovered.Editors[PedalAxis.Brake].LowerDeadzone);
            Assert.Throws<ArgumentException>(() => recovered.Editors[PedalAxis.Brake].BuildProfile());
            Assert.Equal(draft.Profiles[0].Id, recovered.Id);
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public Task RestartOffersRecoveryAndNeverAutomaticallyAppliesRecoveredCurves() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = TempFolder();
        var library = new ProfileLibrary(Path.Combine(folder, "Profiles"));
        var store = new ProfileDraftStore(Path.Combine(folder, "Drafts", "session.json"));
        var fakeDesktop = new SettingsAndTrayTests.FakeDesktop();
        fakeDesktop.Store.Value = new() { StartInTray = true };
        var desktop = fakeDesktop.Services with { Drafts = store };
        try
        {
            var saved = await library.SaveAsync(new("Race", DashboardEngine.DefaultProfile().Axes));
            await library.SetAutoLoadAsync(saved);
            await using (var main = new MainWindow(new DashboardEngine(), desktopServices: desktop, profileLibrary: library))
            {
                await main.InitializeForLaunchAsync();
                var model = (DashboardViewModel)main.DataContext;
                model.Editor.LowerDeadzone = "12";
                model.SetFilterPreset("Smooth");
                model.RefreshUnsavedState();
                Assert.True(model.SelectedProfile!.HasUnsavedChanges);
                Assert.Equal("Race *", model.SelectedProfile.DisplayName);
                Assert.Contains("Unapplied", model.DraftStatus, StringComparison.Ordinal);
                await main.FlushDraftsAsync();
                Assert.Equal(0, (await ProfileStore.LoadAsync(saved)).Axes[PedalAxis.Brake].Deadzone.Lower);
            }
            await using var restarted = new MainWindow(new DashboardEngine(), desktopServices: desktop, profileLibrary: library);
            await restarted.InitializeForLaunchAsync();
            var restored = (DashboardViewModel)restarted.DataContext;
            Assert.True(restored.HasRecoverableDrafts);
            Assert.False(restarted.ShouldStartHidden);
            Assert.Equal("0", restored.Editor.LowerDeadzone);
            Assert.False(restored.HasFilterDraft);
            await restarted.RestoreDraftsAsync();
            Assert.False(restored.HasRecoverableDrafts);
            Assert.Equal("12", restored.Editor.LowerDeadzone);
            Assert.True(restored.HasFilterDraft);
            Assert.True(restored.SelectedAutoLoad);
            Assert.Single(restored.ProfileTabs, tab => tab.FilePath == saved);
            Assert.Contains("Unapplied", restored.DraftStatus, StringComparison.Ordinal);
            restored.Apply();
            Assert.False(restored.HasFilterDraft);
            Assert.True(restored.SelectedProfile!.HasUnsavedChanges);
            Assert.Contains("Unsaved", restored.DraftStatus, StringComparison.Ordinal);
            await restarted.SaveSelectedProfileAsync();
            Assert.False(restored.SelectedProfile.HasUnsavedChanges);
            await restarted.FlushDraftsAsync();
            Assert.Empty((await store.LoadAsync())!.Profiles);
            Assert.Equal(.12, (await ProfileStore.LoadAsync(saved)).Axes[PedalAxis.Brake].Deadzone.Lower);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });

    [Fact]
    public Task IgnoringRecoveryThenClosingKeepsPreviousAndNewDrafts() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = TempFolder();
        try
        {
            var store = new ProfileDraftStore(Path.Combine(folder, "session.json"));
            var prior = new DashboardViewModel(new DashboardEngine()); prior.Editor.LowerDeadzone = "old unfinished";
            await store.SaveAsync(prior.CaptureDrafts());
            await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services with { Drafts = store }, profileLibrary: new(Path.Combine(folder, "Profiles")));
            await main.InitializeForLaunchAsync();
            ((DashboardViewModel)main.DataContext).Editor.UpperDeadzone = "new unfinished";
            await main.FlushDraftsAsync();
            var recovered = (await store.LoadAsync())!;
            Assert.Equal(2, recovered.Profiles.Count);
            Assert.Contains(recovered.Profiles, draft => draft.Editors[PedalAxis.Brake].LowerDeadzone == "old unfinished");
            Assert.Contains(recovered.Profiles, draft => draft.Editors[PedalAxis.Brake].UpperDeadzone == "new unfinished");
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });

    [Fact]
    public Task CorruptRecoveryIsReportedAndNeverOverwrittenOnExit() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = TempFolder(); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "session.json");
        try
        {
            await File.WriteAllTextAsync(path, "{broken recovery");
            await using (var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services with { Drafts = new(path) }, profileLibrary: new(Path.Combine(folder, "Profiles"))))
            {
                await main.InitializeForLaunchAsync();
                Assert.Contains("Draft recovery unavailable", ((DashboardViewModel)main.DataContext).Message, StringComparison.Ordinal);
                ((DashboardViewModel)main.DataContext).Editor.Minimum = "new edits";
                await main.FlushDraftsAsync();
                await Assert.ThrowsAsync<IOException>(() => main.DisposeAsync().AsTask());
                Assert.False(main.IsDisposed);
                ((DashboardViewModel)main.DataContext).Editor.Minimum = "0";
                await main.SaveSelectedProfileAsync();
            }
            Assert.Equal("{broken recovery", await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });

    [Fact]
    public Task DiscardActionRemovesPreviousDraftsOnly() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = TempFolder();
        try
        {
            var store = new ProfileDraftStore(Path.Combine(folder, "session.json"));
            var prior = new DashboardViewModel(new DashboardEngine()); prior.Editor.LowerDeadzone = "9";
            await store.SaveAsync(prior.CaptureDrafts());
            await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services with { Drafts = store }, profileLibrary: new(Path.Combine(folder, "Profiles")));
            await main.InitializeForLaunchAsync();
            var model = (DashboardViewModel)main.DataContext;
            model.Editor.LowerDeadzone = "7";
            ((Button)main.FindName("DiscardDraftsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (!model.CanEdit) { await Task.Delay(5); }
            Assert.False(model.HasRecoverableDrafts);
            var remaining = Assert.Single((await store.LoadAsync())!.Profiles);
            Assert.Equal("7", remaining.Editors[PedalAxis.Brake].LowerDeadzone);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });

    [Fact]
    public async Task UnsupportedAndStructurallyInvalidRecoveryIsRejected()
    {
        var folder = TempFolder(); Directory.CreateDirectory(folder);
        try
        {
            var store = new ProfileDraftStore(Path.Combine(folder, "session.json"));
            foreach (var text in new[] { "{\"Schema\":9,\"Profiles\":[]}", "{\"Schema\":1,\"Profiles\":[null]}", "{\"Schema\":1,\"Profiles\":null}" })
            {
                await File.WriteAllTextAsync(store.FilePath, text);
                await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
                Assert.Equal(text, await File.ReadAllTextAsync(store.FilePath));
            }
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "SherpaAxis-draft-" + Guid.NewGuid().ToString("N"));
}
