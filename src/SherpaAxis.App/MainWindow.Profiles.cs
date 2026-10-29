using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.Profiles;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private Task? libraryInitialization;
    private string? autoLoadProfilePath;
    private int autoLoadRevision;
    private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is not null && viewModel.CanEdit && sender is ComboBox { SelectedItem: ProfileTab tab })
        { Run(() => { StopHold(); viewModel.SwitchProfile(tab); }); }
    }
    private async void LoadLibrary(object sender, RoutedEventArgs e)
    { Loaded -= LoadLibrary; await InitializeLibraryAsync(); }
    private Task InitializeLibraryAsync() => libraryInitialization ??= RunAsync(async () =>
    {
        var profiles = await library.LoadAsync(lifetime.Token);
        autoLoadProfilePath = await library.AutoLoadPathAsync(lifetime.Token);
        foreach (var (path, profile) in profiles) { viewModel.AddSavedProfile(profile, path); }
        var startup = viewModel.ProfileTabs.FirstOrDefault(tab => string.Equals(tab.FilePath, autoLoadProfilePath, StringComparison.OrdinalIgnoreCase) && tab.FilePath is not null);
        if (startup is not null) { viewModel.SwitchProfile(startup); }
        viewModel.SetAutoLoadPath(autoLoadProfilePath);
        if (library.LoadErrors.Count > 0)
        {
            desktop.Log?.Write("profile-load", string.Join("; ", library.LoadErrors));
            viewModel.ShowMessage("Some profiles could not be loaded: " + string.Join("; ", library.LoadErrors));
        }
        await InitializeDraftsAsync();
    });
    private async void AutoLoadProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkbox) { await SetSelectedAutoLoadAsync(checkbox.IsChecked == true); }
    }
    internal Task SetSelectedAutoLoadAsync(bool enabled) => RunAsync(async () =>
    {
        try
        {
            var selected = viewModel.SelectedProfile!;
            if (enabled && selected.FilePath is null)
            {
                var (profile, _) = viewModel.ValidateEditors();
                selected.FilePath = await library.SaveAsync(profile, cancellationToken: lifetime.Token);
                viewModel.MarkSelectedSaved();
            }
            var next = enabled ? selected.FilePath : null;
            await library.SetAutoLoadAsync(next, lifetime.Token);
            autoLoadProfilePath = next;
            autoLoadRevision++;
            viewModel.ShowMessage(enabled ? $"{selected.Name} will load automatically at startup." : "Profile auto-load is off.");
        }
        finally { viewModel.SetAutoLoadPath(autoLoadProfilePath); }
    });
    private async void SaveLibraryClick(object sender, RoutedEventArgs e) => await RunAsync(SaveSelectedProfileAsync);
    internal async Task SaveSelectedProfileAsync()
    {
        var (profile, _) = viewModel.ValidateEditors();
        var selected = viewModel.SelectedProfile!;
        var saved = await library.SaveWithSelectionAsync(profile, selected.FilePath, lifetime.Token);
        selected.FilePath = saved.Path;
        viewModel.MarkSelectedSaved();
        viewModel.SetAutoLoadPath(autoLoadProfilePath);
        viewModel.ShowMessage(saved.SelectionError is null ? "Profile saved."
            : $"Profile saved. Startup selection could not be recorded: {saved.SelectionError}");
        if (saved.SelectionError is not null) { desktop.Log?.Write("profile-selection", saved.SelectionError); }
    }
    private void ProfileMenuClick(object sender, RoutedEventArgs e)
    {
        if (ProfileMoreButton.ContextMenu is not { } menu) { return; }
        menu.PlacementTarget = ProfileMoreButton; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }
    private void DuplicateProfileClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var (profile, _) = viewModel.ValidateEditors();
        viewModel.Load(new(profile.Name.Length <= 73 ? profile.Name + " (copy)" : "Profile copy", profile.Axes));
    });
    private async void RenameProfileClick(object sender, RoutedEventArgs e)
    {
        var name = AskProfileName("Rename profile", viewModel.ProfileName);
        if (name is null) { return; }
        await RunAsync(async () => { viewModel.ProfileName = name; await SaveSelectedProfileAsync(); });
    }
    private string? AskProfileName(string title, string initial)
    {
        var dialog = new ProfileNameDialog(this, title, initial);
        return ShowTextEntryDialog(dialog.ShowDialog) == true ? dialog.ProfileName : null;
    }
    private async void DeleteProfileClick(object sender, RoutedEventArgs e) => await DeleteSelectedProfileAsync();
    private (ProfileTab Tab, int Index, ProfileLibrary.DeletedFile? File, bool AutoLoad, int Revision)? deletedProfile;
    internal Task DeleteSelectedProfileAsync() => RunAsync(async () =>
    {
        var selected = viewModel.SelectedProfile;
        if (selected is null || viewModel.IsCalibrationMode) { return; }
        var file = selected.FilePath is { } path ? await library.DeleteForUndoAsync(path, lifetime.Token) : null;
        var wasAutoLoad = selected.AutoLoad;
        var index = viewModel.ProfileTabs.IndexOf(selected);
        StopHold();
        viewModel.DetachSelectedProfile();
        deletedProfile = (selected, index, file, wasAutoLoad, autoLoadRevision);
        viewModel.CanUndoDelete = true;
        if (selected.FilePath is not null && string.Equals(selected.FilePath, autoLoadProfilePath, StringComparison.OrdinalIgnoreCase))
        { autoLoadProfilePath = null; }
        viewModel.SetAutoLoadPath(autoLoadProfilePath);
        viewModel.ShowMessage($"Deleted profile: {selected.Name}.");
        await FlushDraftsAsync();
    });
    private async void UndoDeleteProfileClick(object sender, RoutedEventArgs e) => await UndoDeleteProfileAsync();
    internal Task UndoDeleteProfileAsync() => RunAsync(async () =>
    {
        if (deletedProfile is not { } deleted) { return; }
        if (deleted.File is not null)
        {
            await library.RestoreDeletedAsync(deleted.File, lifetime.Token);
            deletedProfile = (deleted.Tab, deleted.Index, null, deleted.AutoLoad, deleted.Revision);
        }
        // A newer auto-load selection takes precedence over the deleted profile's old selection.
        if (deleted.AutoLoad && deleted.Revision == autoLoadRevision && await library.AutoLoadPathAsync(lifetime.Token) is null && deleted.Tab.FilePath is { } path)
        { await library.SetAutoLoadAsync(path, lifetime.Token); autoLoadProfilePath = path; }
        StopHold(); viewModel.RestoreDeletedProfile(deleted.Tab, deleted.Index);
        viewModel.SetAutoLoadPath(autoLoadProfilePath);
        deletedProfile = null; viewModel.CanUndoDelete = false;
        viewModel.ShowMessage("Profile restored. Valid settings apply automatically.");
        await FlushDraftsAsync();
    });
    private async void OpenProfileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Localization.Localizer.Translate("SherpaAxis profile (*.json)|*.json"), Title = Localization.Localizer.Translate("Import profile") };
        if (ShowTextEntryDialog(() => dialog.ShowDialog(this)) != true) { return; }
        await RunAsync(async () => { var profile = await ProfileStore.LoadAsync(dialog.FileName, lifetime.Token); StopHold(); viewModel.Load(profile); });
    }
    private async void SaveProfileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = Localization.Localizer.Translate("SherpaAxis profile (*.json)|*.json"), DefaultExt = ".json", FileName = "pedals.json", Title = Localization.Localizer.Translate("Export profile"), OverwritePrompt = true };
        if (ShowTextEntryDialog(() => dialog.ShowDialog(this)) != true) { return; }
        await RunAsync(async () =>
        {
            var (profile, _) = viewModel.ValidateEditors();
            await ProfileStore.SaveAsync(dialog.FileName, profile, overwrite: true, cancellationToken: lifetime.Token);
            viewModel.ShowMessage($"Exported {Path.GetFileName(dialog.FileName)}.");
        });
    }
}
