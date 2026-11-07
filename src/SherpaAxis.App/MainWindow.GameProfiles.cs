using System.IO;
using System.Windows;
using System.Windows.Threading;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.Profiles;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private readonly DispatcherTimer gameProfileTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly GameProfileSelection gameProfileSelection = new();
    private GameBindingStore gameBindingStore = null!;
    private GameProfileBinding[] gameBindings = [];
    private bool gameBindingsLoaded;
    private bool checkingGameProfiles;
    private long gameProcessesCheckedAt = long.MinValue / 2;
    private bool applyingGameProfile;

    private async Task InitializeGameProfilesAsync()
    {
        gameBindingStore = new(library.DirectoryPath);
        try
        {
            gameBindings = await gameBindingStore.LoadAsync(lifetime.Token);
            gameBindingsLoaded = true;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(viewModel.SelectedProfile)) { return; }
                if (!applyingGameProfile && gameProfileSelection.Pending is not null) { gameProfileSelection.Acknowledge(); viewModel.GameProfileStatus = ""; }
                UpdateGameBindingInfo();
            };
            UpdateGameBindingInfo();
            gameProfileTimer.Tick += GameProfileTick;
            if (desktop.GameProcesses is not null) { gameProfileTimer.Start(); }
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        {
            desktop.Log?.Write("game-profiles", "Could not load game bindings.", failure);
            viewModel.ShowMessage("Game bindings unavailable: " + failure.Message);
        }
    }

    private async void GameProfileTick(object? sender, EventArgs e)
    {
        if (checkingGameProfiles || closing) { return; }
        checkingGameProfiles = true;
        try
        {
            if (Environment.TickCount64 - gameProcessesCheckedAt >= 2000)
            {
                gameProcessesCheckedAt = Environment.TickCount64;
                var processes = gameBindings.Length > 0 && desktop.GameProcesses is { } read
                    ? await Task.Run(read, lifetime.Token) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (closing) { return; }
                ObserveGameProcesses(processes);
            }
            TryApplyGameProfile();
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure) || failure is System.ComponentModel.Win32Exception)
        { desktop.Log?.Write("game-profiles", "Game detection could not finish.", failure); }
        finally { checkingGameProfiles = false; }
    }

    internal void ObserveGameProcesses(IReadOnlySet<string> processes)
    {
        var saved = viewModel.ProfileTabs.Where(tab => tab.FilePath is not null).Select(tab => Path.GetFileName(tab.FilePath!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        gameProfileSelection.Observe(gameBindings.Where(binding => saved.Contains(binding.ProfileFileName)), processes);
        var target = viewModel.ProfileTabs.FirstOrDefault(tab => string.Equals(Path.GetFileName(tab.FilePath), gameProfileSelection.Pending, StringComparison.OrdinalIgnoreCase));
        viewModel.GameProfileStatus = gameProfileSelection.Pending is not null && target is not null && target != viewModel.SelectedProfile
            ? $"Game detected · {target.Name} will apply when the pedals are released and the app is ready." : "";
    }

    internal bool TryApplyGameProfile()
    {
        if (gameProfileSelection.Pending is not { } file) { return false; }
        var target = viewModel.ProfileTabs.FirstOrDefault(tab => tab.FilePath is { } path && string.Equals(Path.GetFileName(path), file, StringComparison.OrdinalIgnoreCase));
        if (target is null) { gameProfileSelection.Acknowledge(); viewModel.GameProfileStatus = ""; return false; }
        if (target == viewModel.SelectedProfile) { gameProfileSelection.Acknowledge(); viewModel.GameProfileStatus = ""; return false; }
        if (closing || !launchInitialized || !viewModel.CanTune || viewModel.IsCalibrating || hardwareSession is not null
            || viewModel.Firmware.Running || viewModel.Firmware.Blocked || viewModel.Recovery.IsBlocked
            || viewModel.Updates.IsUpdating || textEntryDialogs > 0 || editingCurve || viewModel.Tour.IsOpen || OwnedWindows.Count > 0 || KeyboardEditingGameProfile())
        { return false; }
        if (!engine.PedalsReleasedForProfileSwitch) { return false; }
        // Validate before selecting so a recovered invalid draft cannot replace the active profile.
        try { _ = new PedalProfile(target.Name, target.Editors.ToDictionary(pair => pair.Key, pair => pair.Value.BuildProfile())); }
        catch (ArgumentException failure)
        {
            gameProfileSelection.Acknowledge();
            viewModel.GameProfileStatus = "";
            viewModel.ShowMessage("Game profile could not be applied: " + failure.Message);
            return false;
        }
        applyingGameProfile = true;
        try
        {
            if (!engine.SwitchProfileWhenReleased(() => { StopHold(); viewModel.SwitchProfile(target); })) { return false; }
        }
        finally { applyingGameProfile = false; }
        gameProfileSelection.Acknowledge();
        viewModel.GameProfileStatus = "";
        UpdateGameBindingInfo();
        viewModel.ShowMessage($"Game profile applied: {target.Name}.");
        desktop.Log?.Write("game-profiles", "Applied profile: " + target.Name);
        return true;
    }

    private bool KeyboardEditingGameProfile() => IsActive && System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
        or System.Windows.Controls.PasswordBox or System.Windows.Controls.ComboBox { IsEditable: true };

    private async void GameBindingsClick(object sender, RoutedEventArgs e)
    {
        if (!gameBindingsLoaded || !viewModel.CanTune || viewModel.SelectedProfile is null) { return; }
        var dialog = CreateGameBindingDialog();
        if (ShowTextEntryDialog(dialog.ShowDialog) == true) { await SetSelectedGameBindingsAsync(dialog.Processes, dialog.Transfers); }
    }

    internal GameBindingDialog CreateGameBindingDialog()
    {
        var selected = viewModel.SelectedProfile ?? throw new InvalidOperationException("Select a profile first.");
        var names = gameBindings.FirstOrDefault(binding => string.Equals(binding.ProfileFileName, Path.GetFileName(selected.FilePath), StringComparison.OrdinalIgnoreCase))?.Processes ?? [];
        var owners = gameBindings.Where(binding => !string.Equals(binding.ProfileFileName, Path.GetFileName(selected.FilePath), StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(library.DirectoryPath, binding.ProfileFileName)))
            .Select(binding => new GameBindingOwner(binding.ProfileFileName,
                viewModel.ProfileTabs.FirstOrDefault(tab => string.Equals(Path.GetFileName(tab.FilePath), binding.ProfileFileName, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? Path.GetFileNameWithoutExtension(binding.ProfileFileName), binding.Processes)).ToArray();
        return new GameBindingDialog(this, selected.Name, names, owners);
    }

    internal Task SetSelectedGameBindingsAsync(string[] processes, IReadOnlyList<GameBindingTransfer>? transfers = null) => RunAsync(async () =>
    {
        if (!gameBindingsLoaded) { throw new InvalidOperationException("Game bindings are unavailable. Check the application log."); }
        var names = processes.Select(GameBindingStore.NormalizeProcess).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = viewModel.SelectedProfile!;
        var current = (await gameBindingStore.LoadAsync(lifetime.Token)).Where(binding => File.Exists(Path.Combine(library.DirectoryPath, binding.ProfileFileName))).ToArray();
        var conflict = current.FirstOrDefault(binding => !string.Equals(binding.ProfileFileName, Path.GetFileName(selected.FilePath), StringComparison.OrdinalIgnoreCase)
            && viewModel.ProfileTabs.Any(tab => string.Equals(Path.GetFileName(tab.FilePath), binding.ProfileFileName, StringComparison.OrdinalIgnoreCase))
            && binding.Processes.Intersect(names, StringComparer.OrdinalIgnoreCase).Any());
        if (transfers is null && conflict is not null)
        {
            var other = viewModel.ProfileTabs.First(tab => string.Equals(Path.GetFileName(tab.FilePath), conflict.ProfileFileName, StringComparison.OrdinalIgnoreCase));
            throw new InvalidOperationException($"A selected game is already bound to {other.Name}. Remove that binding first.");
        }
        // Preflight before auto-saving a new profile. The approved owner must still match the file on disk.
        _ = GameBindingAssignments.Reassign(current, Path.GetFileName(selected.FilePath) ?? Guid.NewGuid().ToString("N") + ".json", names, transfers ?? []);
        if (names.Length > 0 && selected.FilePath is null) { await SaveSelectedProfileAsync(); }
        if (selected.FilePath is not { } path) { return; }
        var next = GameBindingAssignments.Reassign(current, Path.GetFileName(path), names, transfers ?? []);
        await gameBindingStore.SaveAsync(next, lifetime.Token);
        gameBindings = next;
        gameProfileSelection.Reset(); gameProcessesCheckedAt = long.MinValue / 2;
        viewModel.GameProfileStatus = "";
        UpdateGameBindingInfo();
        viewModel.ShowMessage(names.Length > 0 ? "Game bindings saved. This profile will apply when the game starts and the pedals are released." : "Game bindings removed for this profile.");
    });

    private void UpdateGameBindingInfo()
    {
        var names = gameBindings.FirstOrDefault(binding => string.Equals(binding.ProfileFileName, Path.GetFileName(viewModel.SelectedProfile?.FilePath), StringComparison.OrdinalIgnoreCase))?.Processes ?? [];
        viewModel.GameBindingInfo = names.Length > 0 ? string.Join(", ", names.Select(name => name + ".exe")) : "Choose games that automatically apply this profile.";
    }
}
