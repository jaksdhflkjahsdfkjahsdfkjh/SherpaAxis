using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class GameProfileTests
{
    [Fact]
    public async Task BindingsRoundTripWithoutBeingLoadedAsProfiles()
    {
        var folder = TempFolder();
        try
        {
            var library = new ProfileLibrary(folder);
            var path = await library.SaveAsync(DashboardEngine.DefaultProfile());
            var store = new GameBindingStore(folder);
            await store.SaveAsync([new(Path.GetFileName(path), [" acs.exe ", "ACS", "Le Mans Ultimate.exe"])], default);
            var binding = Assert.Single(await store.LoadAsync(default));
            Assert.Equal(["acs", "Le Mans Ultimate"], binding.Processes);
            Assert.Single(await library.LoadAsync());
            Assert.Empty(library.LoadErrors);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync([], cancelled.Token));
            Assert.Single(await store.LoadAsync(default));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("../outside.json", "acs")]
    [InlineData("C:\\outside.json", "acs")]
    [InlineData("profile.txt", "acs")]
    [InlineData("profile.json", "C:\\Game\\game.exe")]
    [InlineData("profile.json", "../game.exe")]
    [InlineData("profile.json", "*.exe")]
    [InlineData("profile.json", ".exe")]
    [InlineData("profile.json", "game\n.exe")]
    [InlineData("profile.json", null)]
    public async Task InvalidBindingsNeverReplaceAnExistingFile(string file, string? process)
    {
        var folder = TempFolder();
        try
        {
            var store = new GameBindingStore(folder);
            await store.SaveAsync([new("saved.json", ["acs"])], default);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([new(file, [process!])], default));
            Assert.Equal("saved.json", Assert.Single(await store.LoadAsync(default)).ProfileFileName);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"SchemaVersion\":2,\"Bindings\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Bindings\":[null]}")]
    [InlineData("{\"SchemaVersion\":1,\"Bindings\":[{\"ProfileFileName\":\"a.json\",\"Processes\":[\"acs\"]},{\"ProfileFileName\":\"b.json\",\"Processes\":[\"ACS.exe\"]}]}")]
    public async Task CorruptOrConflictingBindingFilesAreReported(string json)
    {
        var folder = TempFolder();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, ".game-bindings"), json);
            await Assert.ThrowsAsync<InvalidDataException>(() => new GameBindingStore(folder).LoadAsync(default));
            Assert.Equal(json, await File.ReadAllTextAsync(Path.Combine(folder, ".game-bindings")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void DetectionMatchesExactProcessNamesAndConsumesEachLaunchOnce()
    {
        var selection = new GameProfileSelection();
        GameProfileBinding[] bindings = [new("ac.json", ["acs", "acs_x86"]), new("acc.json", ["AC2-Win64-Shipping"])];
        selection.Observe(bindings, new HashSet<string> { "AssettoCorsa", "Content Manager", "acs-helper" });
        Assert.Null(selection.Pending);
        selection.Observe(bindings, new HashSet<string> { "ACS" });
        Assert.Equal("ac.json", selection.Pending);
        selection.Acknowledge();
        selection.Observe(bindings, new HashSet<string> { "ACS" });
        Assert.Null(selection.Pending); // A manual selection is not overridden on every poll.
        selection.Observe(bindings, new HashSet<string> { "ACS", "ac2-win64-shipping" });
        Assert.Equal("acc.json", selection.Pending);
        selection.Acknowledge();
        selection.Observe(bindings, new HashSet<string> { "ACS", "ac2-win64-shipping" });
        Assert.Null(selection.Pending);
        selection.Observe(bindings, new HashSet<string>());
        Assert.Null(selection.Pending);
        selection.Observe(bindings, new HashSet<string> { "acs_x86" });
        Assert.Equal("ac.json", selection.Pending);
    }

    [Fact]
    public void AGameThatExitsBeforeReleaseCannotLeaveAPendingSwitch()
    {
        var selection = new GameProfileSelection();
        GameProfileBinding[] bindings = [new("race.json", ["game"])];
        selection.Observe(bindings, new HashSet<string> { "game" });
        Assert.NotNull(selection.Pending);
        selection.Observe(bindings, new HashSet<string>());
        Assert.Null(selection.Pending);
    }

    [Fact]
    public Task GameSwitchWaitsForContinuousReleaseAndPreservesThePreviousDraft() => WithWindow(async (main, engine, clock, model, _, target) =>
    {
        model.Editor.LowerDeadzone = "9";
        model.RefreshUnsavedState();
        var original = model.SelectedProfile!;
        engine.Begin(new("live-device-axis"));
        Feed(engine, clock, 0, 0, 0);
        main.ObserveGameProcesses(new HashSet<string> { "ACS" });
        Assert.False(main.TryApplyGameProfile());
        Feed(engine, clock, 100, 0, 0);
        Feed(engine, clock, 200, 0, 8000);
        Feed(engine, clock, 300, 0, 0);
        Feed(engine, clock, 400, 0, 0);
        Assert.False(main.TryApplyGameProfile());
        Feed(engine, clock, 500, 0, 0);
        Feed(engine, clock, 600, 0, 0);
        Assert.True(main.TryApplyGameProfile());
        Assert.Same(target, model.SelectedProfile);
        Assert.Contains("Race", model.ActiveProfileInfo, StringComparison.Ordinal);
        Assert.Equal("9", original.Editors[PedalAxis.Brake].LowerDeadzone);
        Assert.True(original.HasUnsavedChanges);
        Assert.False(main.TryApplyGameProfile());
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData("calibration")]
    [InlineData("software calibration")]
    [InlineData("firmware running")]
    [InlineData("firmware recovery")]
    [InlineData("calibration recovery")]
    [InlineData("busy")]
    [InlineData("updating")]
    [InlineData("stale input")]
    [InlineData("playback")]
    public Task UnsafeStatesDeferWithoutLosingTheLaunch(string state) => WithWindow(async (main, engine, clock, model, _, target) =>
    {
        engine.Begin(new(state == "playback" ? "recording" : "live-device-axis"));
        Release(engine, clock);
        main.ObserveGameProcesses(new HashSet<string> { "acs" });
        switch (state)
        {
            case "calibration": model.IsCalibrationMode = true; break;
            case "software calibration": engine.StartCalibration(PedalAxis.Brake); break;
            case "firmware running": model.Firmware.Running = true; break;
            case "firmware recovery": model.Firmware.Blocked = true; break;
            case "calibration recovery": model.Recovery.Checking(); break;
            case "busy": model.CanEdit = false; break;
            case "updating": model.Updates.IsUpdating = true; break;
            case "stale input": clock.Ticks += 300; break;
        }
        Assert.False(main.TryApplyGameProfile());
        Assert.NotSame(target, model.SelectedProfile);
        model.IsCalibrationMode = false; model.CanEdit = true;
        model.Firmware.Running = model.Firmware.Blocked = model.Updates.IsUpdating = false;
        model.Recovery.Apply([]); engine.CancelCalibration();
        engine.Begin(new("live-device-axis"));
        Release(engine, clock);
        Assert.True(main.TryApplyGameProfile());
        Assert.Same(target, model.SelectedProfile);
        await Task.CompletedTask;
    });

    [Fact]
    public Task ManualSelectionCancelsAPendingAutomaticSwitch() => WithWindow(async (main, engine, clock, model, _, target) =>
    {
        engine.Begin(new("live-device-axis")); Release(engine, clock);
        main.ObserveGameProcesses(new HashSet<string> { "acs" });
        model.Load(new("Manual", DashboardEngine.DefaultProfile().Axes));
        main.ObserveGameProcesses(new HashSet<string> { "acs" });
        Assert.False(main.TryApplyGameProfile()); Assert.NotSame(target, model.SelectedProfile);
        main.ObserveGameProcesses(new HashSet<string>());
        main.ObserveGameProcesses(new HashSet<string> { "acs" });
        Assert.True(main.TryApplyGameProfile());
        await Task.CompletedTask;
    });

    [Fact]
    public Task ADeletedProfileIsIgnoredAndUndoMakesItAvailableAgain() => WithWindow(async (main, engine, clock, model, _, target) =>
    {
        model.SwitchProfile(target);
        await main.DeleteSelectedProfileAsync();
        engine.Begin(new("live-device-axis")); Release(engine, clock);
        main.ObserveGameProcesses(new HashSet<string> { "acs" });
        Assert.False(main.TryApplyGameProfile());
        await main.UndoDeleteProfileAsync();
        model.SwitchProfile(model.ProfileTabs.First(tab => tab.Name == "Road"));
        main.ObserveGameProcesses(new HashSet<string> { "acs" });
        Release(engine, clock);
        Assert.True(main.TryApplyGameProfile());
    });

    [Fact]
    public Task InvalidTargetDraftKeepsTheCurrentProfileActive() => WithWindow(async (main, engine, clock, model, _, target) =>
    {
        target.Editors[PedalAxis.Brake].LowerDeadzone = "unfinished";
        engine.Begin(new("live-device-axis")); Release(engine, clock);
        var original = model.SelectedProfile;
        main.ObserveGameProcesses(new HashSet<string> { "acs" });
        Assert.False(main.TryApplyGameProfile());
        Assert.Same(original, model.SelectedProfile);
        Assert.Contains("Game profile could not be applied", model.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    });

    [Fact]
    public Task BindingEditsPersistRejectConflictsAndCanBeRemoved() => WithWindow(async (main, _, _, model, library, target) =>
    {
        await main.SetSelectedGameBindingsAsync(["ACS.exe"]);
        Assert.Contains("already bound to Race", model.Message, StringComparison.Ordinal);
        var store = new GameBindingStore(library.DirectoryPath);
        Assert.Equal(Path.GetFileName(target.FilePath), Assert.Single(await store.LoadAsync(default)).ProfileFileName);
        await main.SetSelectedGameBindingsAsync(["custom-game.exe"]);
        Assert.Equal(2, (await store.LoadAsync(default)).Length);
        await main.SetSelectedGameBindingsAsync([]);
        Assert.Single(await store.LoadAsync(default));
        model.Load(new("Unsaved", DashboardEngine.DefaultProfile().Axes));
        await main.SetSelectedGameBindingsAsync(["new-game.exe"]);
        Assert.NotNull(model.SelectedProfile!.FilePath);
        Assert.False(model.SelectedProfile.HasUnsavedChanges);
        var restartedClock = new ManualClock();
        var restartedEngine = new DashboardEngine(restartedClock);
        await using var restarted = new MainWindow(restartedEngine, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: library);
        await restarted.InitializeForLaunchAsync();
        Assert.Equal(2, (await store.LoadAsync(default)).Length);
        Assert.Equal("Road", ((DashboardViewModel)restarted.DataContext).SelectedProfile!.Name); // Startup choice is independent.
        restartedEngine.Begin(new("live-device-axis")); Release(restartedEngine, restartedClock);
        restarted.ObserveGameProcesses(new HashSet<string> { "new-game" });
        Assert.True(restarted.TryApplyGameProfile());
        Assert.Equal("Unsaved", ((DashboardViewModel)restarted.DataContext).SelectedProfile!.Name);
    });

    [Fact]
    public Task BackgroundDetectionWorksWhileHiddenAndDoesNotRepeatedlyOverrideManualChoice() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = TempFolder();
        try
        {
            var library = new ProfileLibrary(folder);
            var path = await library.SaveAsync(new("Race", DashboardEngine.DefaultProfile().Axes));
            await new GameBindingStore(folder).SaveAsync([new(Path.GetFileName(path), ["game"])], default);
            var uiThread = Environment.CurrentManagedThreadId;
            var scans = 0;
            var workerThread = 0;
            var clock = new ManualClock();
            var engine = new DashboardEngine(clock);
            var services = new SettingsAndTrayTests.FakeDesktop().Services with
            {
                GameProcesses = () =>
            {
                Interlocked.Increment(ref scans); workerThread = Environment.CurrentManagedThreadId;
                return new HashSet<string> { "game" };
            }
            };
            await using var main = new MainWindow(engine, desktopServices: services, profileLibrary: library);
            await main.InitializeForLaunchAsync();
            var model = (DashboardViewModel)main.DataContext;
            engine.Begin(new("live-device-axis")); Release(engine, clock);
            main.ShowInTaskbar = false; main.Opacity = 0; main.Show();
            Assert.True(await main.HideToTrayAsync());
            Assert.False(main.IsVisible);
            var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(model.SelectedProfile) && model.SelectedProfile?.Name == "Race") { selected.TrySetResult(); }
            };
            await selected.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.NotEqual(uiThread, workerThread);
            Assert.Equal(1, engine.Snapshot(PedalAxis.Brake, false).Connection);
            model.Load(new("Manual", DashboardEngine.DefaultProfile().Axes));
            var firstScan = Volatile.Read(ref scans);
            var until = DateTime.UtcNow.AddSeconds(8);
            while (Volatile.Read(ref scans) <= firstScan && DateTime.UtcNow < until) { await Task.Delay(50); }
            Assert.True(Volatile.Read(ref scans) > firstScan);
            Assert.Equal("Manual", model.SelectedProfile!.Name);
        }
        finally { Directory.Delete(folder, true); }
    });

    [Theory]
    [InlineData("en", 684, 702)]
    [InlineData("uk", 684, 702)]
    [InlineData("en", 524, 442)]
    [InlineData("uk", 524, 442)]
    public Task DialogAndProfileBarUseTheThemeAndFitBothLanguages(string language, double width, double height) => WithWindow(async (main, _, _, model, _, target) =>
    {
        try
        {
            Localizer.Instance.Language = language;
            var content = (FrameworkElement)main.Content;
            content.Measure(new Size(1056, 680)); content.Arrange(new Rect(0, 0, 1056, 680)); content.UpdateLayout();
            var button = Assert.IsType<Button>(main.FindName("GameBindingsButton"));
            Assert.Equal(Localizer.Translate("Game bindings"), button.Content);
            Assert.True(button.ActualWidth + button.Margin.Left + button.Margin.Right >= button.DesiredSize.Width - 1);
            var selector = Assert.IsType<SherpaAxis.App.Controls.StableComboBox>(main.FindName("ProfileSelector"));
            Assert.True(selector.ActualWidth >= 160);
            model.SwitchProfile(target);
            Assert.Equal("acs.exe", model.GameBindingInfo);
            content.UpdateLayout();
            UiRefinementTests.CapturePreview(Assert.IsType<Border>(main.FindName("ProfileBar")), "game-profile-button-" + language + ".png");
            UiRefinementTests.CapturePreview(button, "game-profile-button-closeup-" + language + ".png");
            var dialog = new GameBindingDialog(main, "Race", ["acs", "acs_x86", "custom game"]);
            var panel = Assert.IsType<Grid>(dialog.Content);
            panel.Measure(new Size(width, height));
            panel.Arrange(new Rect(0, 0, width, height)); panel.UpdateLayout();
            var tiles = Assert.IsType<UniformGrid>(dialog.FindName("PresetGames"));
            Assert.True(tiles.Children.OfType<CheckBox>().Single(check => Equals(check.Content, "Assetto Corsa")).IsChecked);
            Assert.Contains("custom game.exe", Assert.IsType<TextBox>(dialog.FindName("CustomExecutables")).Text, StringComparison.Ordinal);
            Assert.False(Assert.IsType<Expander>(dialog.FindName("ExecutableNames")).IsExpanded);
            var save = Assert.IsType<Button>(dialog.FindName("SaveButton"));
            Assert.True(save.ActualHeight >= 36);
            Assert.InRange(save.TranslatePoint(new Point(0, save.ActualHeight), panel).Y, 0, height);
            foreach (var tile in tiles.Children.OfType<CheckBox>())
            {
                tile.ApplyTemplate();
                var icon = Assert.IsType<Image>(tile.Template.FindName("GameIcon", tile));
                var bitmap = Assert.IsType<BitmapImage>(icon.Source);
                Assert.Equal("pack", bitmap.UriSource.Scheme);
                Assert.Equal(32, bitmap.PixelWidth); Assert.Equal(32, bitmap.PixelHeight);
                Assert.True(tile.ActualWidth >= 210);
                var border = Assert.IsType<Border>(tile.Template.FindName("Tile", tile));
                if (tile.IsChecked == true) { Assert.Equal(SherpaAxis.App.Themes.AppColors.Accent, Assert.IsType<SolidColorBrush>(border.BorderBrush).Color); }
            }
            UiRefinementTests.CapturePreview(panel, "game-bindings-" + language + (width < 600 ? "-small" : "") + ".png");
            dialog.Close();
            await Task.CompletedTask;
        }
        finally { Localizer.Instance.Language = "en"; }
    });

    [Fact]
    public Task GameTilesAndCustomRowsPreserveAndRemoveExecutableBindings() => WithWindow(async (main, _, _, _, _, _) =>
    {
        var dialog = new GameBindingDialog(main, "Race", ["ACS", "custom.exe"]);
        try
        {
            var tiles = Assert.IsType<UniformGrid>(dialog.FindName("PresetGames"));
            var ac = tiles.Children.OfType<CheckBox>().Single(check => Equals(check.Content, "Assetto Corsa"));
            Assert.False(ac.IsChecked); // A partial existing preset is not silently expanded to include acs_x86.
            Assert.Equal(["ACS", "custom"], dialog.SelectedProcesses());
            dialog.AddExecutable(@"C:\Games\acs.exe");
            Assert.True(ac.IsChecked);
            Assert.Contains("acs_x86", dialog.SelectedProcesses());
            ac.IsChecked = false;
            Assert.DoesNotContain(dialog.SelectedProcesses(), name => name.Equals("acs", StringComparison.OrdinalIgnoreCase) || name == "acs_x86");
            ac.IsChecked = true;
            var other = Assert.IsType<StackPanel>(dialog.FindName("AdditionalGames"));
            var row = other.Children.OfType<DockPanel>().Single(item => Equals(item.Tag, "custom.exe"));
            row.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.DoesNotContain("custom", dialog.SelectedProcesses());
            dialog.AddExecutable(@"D:\Racing\new-game.exe");
            dialog.AddExecutable(@"D:\Racing\NEW-GAME.exe");
            Assert.Equal(1, dialog.SelectedProcesses().Count(name => name == "new-game"));
            var custom = Assert.IsType<TextBox>(dialog.FindName("CustomExecutables"));
            custom.Text = "*.exe";
            dialog.AddExecutable(@"D:\Racing\another-game.exe"); // An unfinished invalid entry does not crash the file picker.
            Assert.Contains("another-game.exe", custom.Text, StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(dialog.SelectedProcesses);
        }
        finally { dialog.Close(); }
        await Task.CompletedTask;
    });

    private static Task WithWindow(Func<MainWindow, DashboardEngine, ManualClock, DashboardViewModel, ProfileLibrary, ProfileTab, Task> action)
        => HardwarePedalUiTests.OnUiAsync(async () =>
        {
            var folder = TempFolder();
            try
            {
                var library = new ProfileLibrary(folder);
                var first = await library.SaveAsync(new("Road", DashboardEngine.DefaultProfile().Axes));
                var second = await library.SaveAsync(new("Race", DashboardEngine.DefaultProfile().Axes));
                await library.SetAutoLoadAsync(first);
                await new GameBindingStore(folder).SaveAsync([new(Path.GetFileName(second), ["acs"])], default);
                var clock = new ManualClock();
                var engine = new DashboardEngine(clock);
                await using var main = new MainWindow(engine, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: library);
                await main.InitializeForLaunchAsync();
                var model = (DashboardViewModel)main.DataContext;
                await action(main, engine, clock, model, library, model.ProfileTabs.Single(tab => tab.Name == "Race"));
            }
            finally { Directory.Delete(folder, true); }
        });

    private static void Feed(DashboardEngine engine, ManualClock clock, long time, int brake, ushort clutch)
    {
        clock.Ticks = time;
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(time), 1, new(0, brake, clutch)));
    }
    private static void Release(DashboardEngine engine, ManualClock clock)
    {
        var start = clock.Ticks + 100;
        for (var time = start; time <= start + 400; time += 100) { Feed(engine, clock, time, 0, 0); }
    }
    private static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "SherpaAxis-game-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private sealed class ManualClock : TimeProvider
    {
        internal long Ticks { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
    }
}
