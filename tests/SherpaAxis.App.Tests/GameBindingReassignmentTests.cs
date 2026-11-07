using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class GameBindingReassignmentTests
{
    [Theory]
    [InlineData("acs")]
    [InlineData("ACS_X86.exe")]
    public void ReassignMovesAllKnownAliasesAndPreservesOtherGames(string process)
    {
        GameProfileBinding[] current = [new("old.json", ["acs", "acs_x86", "unrelated"]), new("new.json", ["other-game"]), new("third.json", ["third-game"])];
        GameBindingOwner[] owners = [new("old.json", "Dry", ["acs", "acs_x86", "unrelated"]), new("third.json", "Third", ["third-game"])];
        var requested = new[] { process, "other-game" };
        var approved = GameBindingAssignments.Conflicts(requested, owners);
        var next = GameBindingAssignments.Reassign(current, "new.json", requested, approved);
        Assert.Equal(["unrelated"], next.Single(binding => binding.ProfileFileName == "old.json").Processes);
        Assert.Equal(["third-game"], next.Single(binding => binding.ProfileFileName == "third.json").Processes);
        Assert.Equal(["acs", "acs_x86", "other-game"], next.Single(binding => binding.ProfileFileName == "new.json").Processes.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["acs", "acs_x86", "unrelated"], current[0].Processes);
    }

    [Fact]
    public void ATransferRequiresApprovalForTheCurrentOwnerAndEveryAlias()
    {
        GameProfileBinding[] current = [new("changed.json", ["acs"]), new("old.json", ["acs_x86"])];
        Assert.Throws<InvalidOperationException>(() => GameBindingAssignments.Reassign(current, "new.json", ["acs"], [new("acs", "old.json")]));
        var next = GameBindingAssignments.Reassign(current, "new.json", ["ACS.exe"], [new("ACS", "CHANGED.JSON"), new("ACS_X86", "OLD.JSON")]);
        var target = Assert.Single(next);
        Assert.Equal("new.json", target.ProfileFileName);
        Assert.Equal(2, target.Processes.Length);
    }

    [Theory]
    [InlineData("en", 684, 702)]
    [InlineData("uk", 684, 702)]
    [InlineData("en", 524, 442)]
    [InlineData("uk", 524, 442)]
    public Task CardShowsOwnerAndPreparesTransferWithoutWritingUntilSave(string language, double width, double height)
        => WithProfiles(async (main, library, store, oldPath, targetPath, _) =>
        {
            try
            {
                Localizer.Instance.Language = language;
                var bytes = await File.ReadAllBytesAsync(Path.Combine(library.DirectoryPath, ".game-bindings"));
                var originalProfile = await File.ReadAllBytesAsync(oldPath);
                var targetProfile = await File.ReadAllBytesAsync(targetPath);
                var dialog = main.CreateGameBindingDialog();
                try
                {
                    var root = (FrameworkElement)dialog.Content;
                    void Arrange() { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
                    Arrange();
                    var card = Assert.IsType<UniformGrid>(dialog.FindName("PresetGames")).Children.OfType<CheckBox>().Single(check => Equals(check.Content, "Assetto Corsa Competizione"));
                    card.ApplyTemplate();
                    Assert.Contains("Dry", Assert.IsType<TextBlock>(card.Template.FindName("NotSelected", card)).Text, StringComparison.Ordinal);
                    var move = Assert.IsType<Button>(card.Template.FindName("ReassignButton", card));
                    Assert.Equal(Visibility.Visible, move.Visibility);
                    Assert.Equal(Localizer.Translate("Reassign here"), move.Content);
                    Assert.True(move.ActualWidth <= card.ActualWidth - 90);
                    var suffix = language + (width < 600 ? "-small" : "");
                    UiRefinementTests.CapturePreview(root, "game-reassign-before-" + suffix + ".png");
                    move.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Arrange();
                    Assert.True(card.IsChecked);
                    Assert.Equal(Visibility.Collapsed, move.Visibility);
                    Assert.Equal(Localizer.Translate("Will be bound to this profile"), Assert.IsType<TextBlock>(card.Template.FindName("Selected", card)).Text);
                    Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(library.DirectoryPath, ".game-bindings")));
                    Assert.Equal(originalProfile, await File.ReadAllBytesAsync(oldPath));
                    Assert.Equal(targetProfile, await File.ReadAllBytesAsync(targetPath));
                    Arrange();
                    UiRefinementTests.CapturePreview(root, "game-reassign-pending-" + suffix + ".png");
                    var processes = dialog.SelectedProcesses();
                    var transfers = dialog.SelectedTransfers();
                    dialog.Close();
                    await main.SetSelectedGameBindingsAsync(processes, transfers);
                    var saved = await store.LoadAsync(default);
                    Assert.DoesNotContain("AC2-Win64-Shipping", saved.Single(binding => binding.ProfileFileName == Path.GetFileName(oldPath)).Processes);
                    Assert.Contains("AC2-Win64-Shipping", saved.Single(binding => binding.ProfileFileName == Path.GetFileName(targetPath)).Processes);
                    Assert.Contains("unrelated", saved.Single(binding => binding.ProfileFileName == Path.GetFileName(oldPath)).Processes);
                    Assert.Equal(originalProfile, await File.ReadAllBytesAsync(oldPath));
                    Assert.Equal(targetProfile, await File.ReadAllBytesAsync(targetPath));
                    Assert.Equal(oldPath, await library.AutoLoadPathAsync());
                }
                finally { dialog.Close(); }
            }
            finally { Localizer.Instance.Language = "en"; }
        });

    [Fact]
    public Task CancelDiscardsTheProposedMove() => WithProfiles(async (main, library, store, oldPath, _, _) =>
    {
        var before = await File.ReadAllBytesAsync(Path.Combine(library.DirectoryPath, ".game-bindings"));
        var dialog = main.CreateGameBindingDialog();
        dialog.AddExecutable(@"C:\Games\AC2-Win64-Shipping.exe");
        Assert.Single(dialog.SelectedTransfers());
        dialog.Close();
        Assert.Empty(dialog.Transfers);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(library.DirectoryPath, ".game-bindings")));
        Assert.Contains("AC2-Win64-Shipping", (await store.LoadAsync(default)).Single(binding => binding.ProfileFileName == Path.GetFileName(oldPath)).Processes);
    });

    [Fact]
    public Task FailedWriteLeavesEveryAssignmentAndProfileUnchanged() => WithProfiles(async (main, library, store, oldPath, targetPath, _) =>
    {
        var file = Path.Combine(library.DirectoryPath, ".game-bindings");
        var before = await File.ReadAllBytesAsync(file);
        var oldProfile = await File.ReadAllBytesAsync(oldPath);
        var targetProfile = await File.ReadAllBytesAsync(targetPath);
        var dialog = main.CreateGameBindingDialog();
        dialog.AddExecutable(@"C:\Games\acs.exe");
        var processes = dialog.SelectedProcesses(); var approved = dialog.SelectedTransfers(); dialog.Close();
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try { await main.SetSelectedGameBindingsAsync(processes, approved); }
        finally { File.SetAttributes(file, FileAttributes.Normal); }
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Equal(oldProfile, await File.ReadAllBytesAsync(oldPath));
        Assert.Equal(targetProfile, await File.ReadAllBytesAsync(targetPath));
        Assert.DoesNotContain(await store.LoadAsync(default), binding => binding.ProfileFileName == Path.GetFileName(targetPath));
        Assert.True(((DashboardViewModel)main.DataContext).CanEdit);
        // Retrying the same reviewed transfer after fixing storage succeeds.
        await main.SetSelectedGameBindingsAsync(processes, approved);
        Assert.Contains(await store.LoadAsync(default), binding => binding.ProfileFileName == Path.GetFileName(targetPath));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FreshAssignmentsAreMergedAndChangedOwnersRequireReview(bool changeOwner) => WithProfiles(async (main, library, store, oldPath, targetPath, thirdPath) =>
    {
        var dialog = main.CreateGameBindingDialog();
        dialog.AddExecutable(@"C:\Games\AC2-Win64-Shipping.exe");
        var processes = dialog.SelectedProcesses(); var approved = dialog.SelectedTransfers(); dialog.Close();
        var latest = (await store.LoadAsync(default)).Select(binding => changeOwner
            ? binding with { Processes = binding.Processes.Where(process => process != "AC2-Win64-Shipping").ToArray() } : binding).ToList();
        latest.Add(new(Path.GetFileName(thirdPath), changeOwner ? ["AC2-Win64-Shipping", "new-game"] : ["new-game"]));
        await store.SaveAsync(latest.ToArray(), default);
        var before = await File.ReadAllBytesAsync(Path.Combine(library.DirectoryPath, ".game-bindings"));
        await main.SetSelectedGameBindingsAsync(processes, approved);
        var result = await store.LoadAsync(default);
        if (changeOwner)
        {
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(library.DirectoryPath, ".game-bindings")));
            Assert.Contains("Game bindings changed", ((DashboardViewModel)main.DataContext).Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("AC2-Win64-Shipping", result.Single(binding => binding.ProfileFileName == Path.GetFileName(targetPath)).Processes);
            Assert.DoesNotContain("AC2-Win64-Shipping", result.Single(binding => binding.ProfileFileName == Path.GetFileName(oldPath)).Processes);
        }
        Assert.Contains("new-game", result.Single(binding => binding.ProfileFileName == Path.GetFileName(thirdPath)).Processes);
    });

    private static Task WithProfiles(Func<MainWindow, ProfileLibrary, GameBindingStore, string, string, string, Task> action)
        => HardwarePedalUiTests.OnUiAsync(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-reassign-" + Guid.NewGuid().ToString("N"));
            var library = new ProfileLibrary(folder);
            try
            {
                var oldPath = await library.SaveAsync(new("Dry", DashboardEngine.DefaultProfile().Axes));
                var targetPath = await library.SaveAsync(new("Rain", DashboardEngine.DefaultProfile().Axes));
                var thirdPath = await library.SaveAsync(new("Third", DashboardEngine.DefaultProfile().Axes));
                await library.SetAutoLoadAsync(oldPath);
                var store = new GameBindingStore(folder);
                await store.SaveAsync([new(Path.GetFileName(oldPath), ["acs", "acs_x86", "AC2-Win64-Shipping", "unrelated"])], default);
                await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: library);
                await main.InitializeForLaunchAsync();
                var model = (DashboardViewModel)main.DataContext;
                model.SwitchProfile(model.ProfileTabs.Single(tab => tab.FilePath == targetPath));
                await action(main, library, store, oldPath, targetPath, thirdPath);
            }
            finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
        });
}
