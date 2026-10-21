using System.IO;
using System.Windows;
using System.Windows.Controls;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class ProfileDeletionTests
{
    [Fact]
    public Task UndoRestoresSavedBytesAndUnfinishedEditorsWithoutApplyingThem() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-undo-" + Guid.NewGuid().ToString("N"));
        var library = new ProfileLibrary(directory);
        try
        {
            var path = await library.SaveAsync(new("Race", DashboardEngine.DefaultProfile().Axes));
            var original = await File.ReadAllBytesAsync(path);
            await library.SetAutoLoadAsync(path);
            await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: library);
            await main.InitializeForLaunchAsync();
            var model = (DashboardViewModel)main.DataContext;
            var tab = model.SelectedProfile;
            model.Editor.LowerDeadzone = "unfinished";
            await main.DeleteSelectedProfileAsync();
            Assert.False(File.Exists(path)); Assert.True(model.CanUndoDelete);
            await main.UndoDeleteProfileAsync();
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Same(tab, model.SelectedProfile);
            Assert.Equal("unfinished", model.Editor.LowerDeadzone);
            Assert.True(model.SelectedAutoLoad);
            Assert.False(model.CanUndoDelete);
            Assert.Throws<ArgumentException>(() => model.Apply());
            Assert.Equal("Applied: " + DashboardEngine.DefaultProfile().Name, model.ActiveProfileInfo);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task UndoDoesNotReplaceANewerAutoLoadChoiceOrOverwriteAnExistingFile(bool keepAutoLoad) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-undo-conflict-" + Guid.NewGuid().ToString("N"));
        var library = new ProfileLibrary(directory);
        try
        {
            var first = await library.SaveAsync(new("First", DashboardEngine.DefaultProfile().Axes));
            var second = await library.SaveAsync(new("Second", DashboardEngine.DefaultProfile().Axes));
            await library.SetAutoLoadAsync(first);
            await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: library);
            await main.InitializeForLaunchAsync();
            var model = (DashboardViewModel)main.DataContext;
            var deleted = model.SelectedProfile!;
            await main.DeleteSelectedProfileAsync();
            model.SwitchProfile(model.ProfileTabs.Single(tab => tab.FilePath == second));
            await main.SetSelectedAutoLoadAsync(true);
            if (!keepAutoLoad) { await main.SetSelectedAutoLoadAsync(false); }
            await File.WriteAllTextAsync(first, "external replacement");
            await main.UndoDeleteProfileAsync();
            Assert.Equal("external replacement", await File.ReadAllTextAsync(first));
            Assert.DoesNotContain(deleted, model.ProfileTabs);
            Assert.True(model.CanUndoDelete);
            Assert.Equal(keepAutoLoad ? second : null, await library.AutoLoadPathAsync());
            File.Delete(first);
            await main.UndoDeleteProfileAsync();
            Assert.Contains(deleted, model.ProfileTabs);
            Assert.False(model.SelectedAutoLoad);
            Assert.Equal(keepAutoLoad ? second : null, await library.AutoLoadPathAsync());
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });

    [Fact]
    public Task UndoAlsoRestoresANeverSavedProfileAndItsCurveDrafts() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var model = (DashboardViewModel)main.DataContext;
        model.SetCurvePreset(SherpaAxis.Profiles.PedalAxis.Clutch, "Aggressive");
        var tab = model.SelectedProfile;
        await main.DeleteSelectedProfileAsync();
        await main.UndoDeleteProfileAsync();
        Assert.Same(tab, model.SelectedProfile);
        model.SelectedAxis = SherpaAxis.Profiles.PedalAxis.Clutch;
        Assert.Equal("Aggressive", model.Editor.CurveKind);
        Assert.Null(model.SelectedProfile!.FilePath);
        Assert.All(model.Curve, point => Assert.Equal(Math.Sqrt(point.Input), point.Output, 8));
    });
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SavedDeletionUpdatesTheLibraryAutoLoadAndRestart(bool deleteAutoLoad) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-delete-" + Guid.NewGuid().ToString("N"));
        var library = new ProfileLibrary(directory);
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        try
        {
            var first = await library.SaveAsync(new("First", DashboardEngine.DefaultProfile().Axes));
            var second = await library.SaveAsync(new("Second", DashboardEngine.DefaultProfile().Axes));
            await library.SetAutoLoadAsync(second);
            await using (var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services, profileLibrary: library))
            {
                await main.InitializeForLaunchAsync();
                var model = Assert.IsType<DashboardViewModel>(main.DataContext);
                if (!deleteAutoLoad) { model.SwitchProfile(model.ProfileTabs.Single(tab => tab.Name == "First")); }
                var removed = model.SelectedProfile!;
                await main.DeleteSelectedProfileAsync();
                Assert.DoesNotContain(removed, model.ProfileTabs);
                Assert.Equal(deleteAutoLoad ? "First" : "Second", model.SelectedProfile!.Name);
                Assert.False(File.Exists(deleteAutoLoad ? second : first));
                Assert.True(File.Exists(deleteAutoLoad ? first : second));
                Assert.Equal(deleteAutoLoad ? null : second, await library.AutoLoadPathAsync());
                Assert.Equal(!deleteAutoLoad, model.SelectedAutoLoad);
            }
            await using var restarted = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services, profileLibrary: library);
            await restarted.InitializeForLaunchAsync();
            var restored = Assert.IsType<DashboardViewModel>(restarted.DataContext);
            Assert.DoesNotContain(restored.ProfileTabs, tab => tab.Name == (deleteAutoLoad ? "Second" : "First"));
            Assert.Equal(deleteAutoLoad ? DashboardEngine.DefaultProfile().Name : "Second", restored.SelectedProfile!.Name);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });

    [Fact]
    public Task MenuDeletesTheLastDraftAndCreatesFreshDefaults() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        var removed = model.SelectedProfile!;
        var menu = Assert.IsType<Button>(main.FindName("ProfileMoreButton")).ContextMenu;
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Delete profile")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.NotSame(removed, Assert.Single(model.ProfileTabs));
        Assert.Equal(DashboardEngine.DefaultProfile().Name, model.SelectedProfile!.Name);
        Assert.Equal("0", model.Editor.Minimum);
        Assert.False(model.HasFilterDraft);
        Assert.Null(model.SelectedProfile.FilePath);
        Assert.Empty(model.CaptureDrafts().Profiles);
    });

    [Fact]
    public void InvalidRemainingDraftIsPreservedButNeverKeepsTheDeletedProfileActive()
    {
        var model = new DashboardViewModel(new DashboardEngine());
        var unfinished = model.SelectedProfile!;
        model.Editor.Minimum = "unfinished";
        model.Load(new("Delete me", DashboardEngine.DefaultProfile().Axes));
        var removed = model.SelectedProfile!;
        model.RemoveSelectedProfile();
        Assert.DoesNotContain(removed, model.ProfileTabs);
        Assert.Contains(unfinished, model.ProfileTabs);
        Assert.Equal(DashboardEngine.DefaultProfile().Name, model.SelectedProfile!.Name);
        Assert.Equal("Applied: " + DashboardEngine.DefaultProfile().Name, model.ActiveProfileInfo);
        _ = model.ValidateEditors();
        model.SwitchProfile(unfinished);
        Assert.Equal("unfinished", model.Editor.Minimum);
    }

    [Fact]
    public async Task DeletionRejectsOutsidePathsAndCancellationWithoutRemovingData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-delete-" + Guid.NewGuid().ToString("N"));
        var library = new ProfileLibrary(Path.Combine(directory, "Profiles"));
        try
        {
            var saved = await library.SaveAsync(DashboardEngine.DefaultProfile());
            await library.SetAutoLoadAsync(saved);
            var outside = Path.Combine(directory, "outside.json");
            await File.WriteAllTextAsync(outside, "original import");
            await Assert.ThrowsAsync<ArgumentException>(() => library.DeleteAsync(outside));
            Assert.Equal("original import", await File.ReadAllTextAsync(outside));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.DeleteAsync(saved, cancelled.Token));
            Assert.True(File.Exists(saved)); Assert.Equal(saved, await library.AutoLoadPathAsync());
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Fact]
    public Task FailedFileDeletionKeepsTheSelectedProfileAndAutoLoad() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-delete-" + Guid.NewGuid().ToString("N"));
        var library = new ProfileLibrary(directory);
        var saved = await library.SaveAsync(DashboardEngine.DefaultProfile());
        try
        {
            await library.SetAutoLoadAsync(saved);
            await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: library);
            await main.InitializeForLaunchAsync();
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            var selected = model.SelectedProfile;
            File.SetAttributes(saved, FileAttributes.ReadOnly);
            await main.DeleteSelectedProfileAsync();
            Assert.Same(selected, model.SelectedProfile);
            Assert.True(File.Exists(saved));
            Assert.True(model.SelectedAutoLoad && model.CanEdit);
            Assert.Equal(saved, await library.AutoLoadPathAsync());
        }
        finally
        {
            if (File.Exists(saved)) { File.SetAttributes(saved, FileAttributes.Normal); }
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    });
}
