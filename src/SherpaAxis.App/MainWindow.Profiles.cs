using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SherpaAxis.App.Models;
using SherpaAxis.Profiles;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private readonly SemaphoreSlim profileSelectionGate = new(1, 1);
    private readonly List<Task> profileSelections = [];
    private void StarterProfilesClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        StopHold();
        foreach (var profile in StarterProfiles.Create()) { viewModel.Load(profile); }
        viewModel.ShowMessage("Four starter profile tabs added. Device-axis calibration defaults; review curves and Save to library to keep a tab.");
    });
    private async void ProfileTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is not null && sender is ListBox { SelectedItem: ProfileTab tab })
        {
            Run(() => { StopHold(); viewModel.SwitchProfile(tab); });
            if (tab.FilePath is { } path && viewModel.CanEdit && !closing)
            {
                var operation = RememberProfileAsync(tab, path);
                profileSelections.Add(operation);
                try { await operation; }
                finally { profileSelections.Remove(operation); }
            }
        }
    }

    private async Task RememberProfileAsync(ProfileTab tab, string path)
    {
        try
        {
            await profileSelectionGate.WaitAsync(lifetime.Token);
            try { if (viewModel.SelectedProfile == tab) { await library.RememberAsync(path, lifetime.Token); } }
            finally { profileSelectionGate.Release(); }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            if (!closing) { viewModel.ShowMessage($"Profile selected; could not remember selection: {exception.Message}"); }
        }
    }
    private void DuplicateProfileClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var (profile, _) = viewModel.ValidateEditors();
        StopHold();
        viewModel.Load(new PedalProfile(profile.Name.Length <= 73 ? profile.Name + " (copy)" : "Profile copy", profile.Axes));
    });
    private async void SaveLibraryClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var (profile, _) = viewModel.ValidateEditors();
        var tab = viewModel.SelectedProfile!;
        tab.FilePath = await library.SaveAsync(profile, tab.FilePath, lifetime.Token);
        StopHold();
        viewModel.Apply();
        viewModel.ShowMessage("Profile saved to the local library. It will be available as a tab after restart.");
    });
    private async void LoadLibrary(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadLibrary;
        await RunAsync(async () =>
        {
            var profiles = await library.LoadAsync(lifetime.Token);
            var selected = await library.SelectedPathAsync(lifetime.Token);
            foreach (var (path, profile) in profiles)
            {
                viewModel.Load(profile);
                viewModel.SelectedProfile!.FilePath = path;
            }
            var active = viewModel.ProfileTabs.FirstOrDefault(tab => tab.FilePath == selected);
            if (active is not null) { viewModel.SwitchProfile(active); }
            if (library.LoadErrors.Count > 0) { viewModel.ShowMessage("Some library profiles could not be loaded: " + string.Join("; ", library.LoadErrors)); }
        });
    }
    private async void OpenProfileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SherpaAxis profile (*.json)|*.json", Title = "Open processing profile" };
        if (dialog.ShowDialog(this) == true)
        {
            await RunAsync(async () =>
            {
                var profile = await ProfileStore.LoadAsync(dialog.FileName, lifetime.Token);
                StopHold();
                viewModel.Load(profile);
            });
        }
    }

    private async void SaveProfileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "SherpaAxis profile (*.json)|*.json", DefaultExt = ".json", FileName = "pedals.json", Title = "Save processing profile", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == true)
        {
            await RunAsync(async () =>
            {
                var (profile, _) = viewModel.ValidateEditors();
                await ProfileStore.SaveAsync(dialog.FileName, profile, overwrite: true, cancellationToken: lifetime.Token);
                StopHold();
                viewModel.Apply();
                viewModel.ShowMessage($"Saved {Path.GetFileName(dialog.FileName)} and applied software settings. Runtime correction is not saved.");
            });
        }
    }

}
