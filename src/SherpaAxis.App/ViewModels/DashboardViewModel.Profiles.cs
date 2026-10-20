using System.Collections.ObjectModel;
using SherpaAxis.App.Models;
using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

public sealed partial class DashboardViewModel
{
    public ObservableCollection<ProfileTab> ProfileTabs { get; } = [];
    public ProfileTab? SelectedProfile => selectedProfile;
    private string? autoLoadPath;
    public bool SelectedAutoLoad => selectedProfile?.FilePath is { } path && string.Equals(path, autoLoadPath, StringComparison.OrdinalIgnoreCase);
    public void SetAutoLoadPath(string? path)
    {
        autoLoadPath = path;
        foreach (var tab in ProfileTabs) { tab.AutoLoad = tab.FilePath is { } file && string.Equals(file, path, StringComparison.OrdinalIgnoreCase); }
        Changed(nameof(SelectedAutoLoad));
    }
    public ProfileTab AddSavedProfile(PedalProfile profile, string path)
    {
        var tab = new ProfileTab { Name = profile.Name, FilePath = path };
        var defaults = DashboardEngine.DefaultProfile();
        foreach (var axis in AxisNames) { tab.Editors[axis] = AxisEditor.FromProfile(profile.Axes.TryGetValue(axis, out var settings) ? settings : defaults.Axes[axis]); }
        ProfileTabs.Add(tab);
        tab.MarkSaved();
        return tab;
    }

    public string ProfileName
    {
        get => profileName;
        set { if (profileName == value) { return; } profileName = value; if (selectedProfile is not null) { selectedProfile.Name = value; } Changed(); }
    }

    public (PedalProfile Profile, IReadOnlyDictionary<PedalAxis, AxisSessionSettings> Limits) ValidateEditors()
    {
        var profiles = new Dictionary<PedalAxis, AxisProfile>();
        var limits = new Dictionary<PedalAxis, AxisSessionSettings>();
        foreach (var (axis, editor) in editors)
        {
            try
            {
                profiles.Add(axis, editor.BuildProfile());
                limits.Add(axis, editor.BuildSessionSettings());
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"{axis}: {exception.Message}", exception);
            }
        }

        return (new(ProfileName, profiles), limits);
    }

    public void Load(PedalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        StoreDraft();
        CancelCalibration();
        selectedProfile = new ProfileTab { Name = profile.Name };
        ProfileTabs.Add(selectedProfile);
        var defaults = DashboardEngine.DefaultProfile();
        foreach (var axis in AxisNames)
        {
            editors[axis] = AxisEditor.FromProfile(profile.Axes.TryGetValue(axis, out var value) ? value : defaults.Axes[axis]);
        }

        ProfileName = profile.Name;
        Apply();
        RefreshDraftCurve();
        Changed(nameof(SelectedProfile));
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage("Profile loaded. Missing axes use device-range defaults. Check that calibration matches this input source.");
    }

    public void SwitchProfile(ProfileTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab == selectedProfile || !ProfileTabs.Contains(tab)) { return; }
        StoreDraft();
        engine.RevokeRelease();
        engine.SuspendOutput("profile switched");
        CancelCalibration();
        selectedProfile = tab;
        foreach (var (axis, editor) in tab.Editors) { editors[axis] = editor with { }; }
        ProfileName = tab.Name;
        try
        {
            if (!tab.RequiresApply) { Apply(); }
            else { ShowMessage("Recovered draft selected. Review it and Apply settings to activate it."); }
        }
        catch (ArgumentException exception) { ShowMessage($"Draft restored; previous processing remains active until Apply: {exception.Message}"); }
        RefreshDraftCurve();
        Changed(nameof(SelectedProfile));
        Changed(nameof(Editor));
        NotifyFilterSettings();
        Refresh();
        RefreshUnsavedState();
    }

    private bool hasRecoverableDrafts;
    public bool HasRecoverableDrafts { get => hasRecoverableDrafts; set => Set(ref hasRecoverableDrafts, value); }
    private bool canUndoDelete;
    public bool CanUndoDelete { get => canUndoDelete; set => Set(ref canUndoDelete, value); }
    private string draftStatus = "";
    public string DraftStatus { get => draftStatus; private set => Set(ref draftStatus, value); }

    public void RefreshUnsavedState()
    {
        StoreDraft();
        foreach (var tab in ProfileTabs) { tab.RefreshUnsavedState(); }
        var pending = HasUnappliedEdits();
        DraftStatus = pending ? "Unapplied edits · Apply settings to activate"
            : selectedProfile?.HasUnsavedChanges == true ? "Unsaved profile · Save to keep these settings"
            : selectedProfile?.FilePath is null ? "Default settings · Save to create a profile" : "Profile saved · Settings applied";
    }

    private readonly Dictionary<PedalAxis, AxisEditor> assessedEditors = [];
    private PedalProfile? assessedApplied;
    private string? assessedName;
    private bool assessedPending;

    private bool HasUnappliedEdits()
    {
        if (ReferenceEquals(assessedApplied, applied) && assessedName == ProfileName
            && editors.Count == assessedEditors.Count
            && editors.All(pair => assessedEditors.TryGetValue(pair.Key, out var editor) && pair.Value == editor))
        { return assessedPending; }

        assessedPending = true;
        try { assessedPending = !ProfileJson.Serialize(ValidateEditors().Profile).AsSpan().SequenceEqual(ProfileJson.Serialize(applied)); }
        catch (ArgumentException) { }
        assessedApplied = applied;
        assessedName = ProfileName;
        assessedEditors.Clear();
        foreach (var (axis, editor) in editors) { assessedEditors[axis] = editor with { }; }
        return assessedPending;
    }

    public void MarkSelectedSaved()
    {
        StoreDraft();
        if (selectedProfile is not null) { selectedProfile.MarkSaved(); }
        RefreshUnsavedState();
    }
    public ProfileDraftSession CaptureDrafts()
    {
        RefreshUnsavedState();
        return new(1, ProfileTabs.Where(tab => tab.HasUnsavedChanges && tab.Recoverable).Select(tab => new ProfileDraft(tab.DraftId,
            tab.Name, tab.FilePath is null ? null : System.IO.Path.GetFileName(tab.FilePath), tab.Editors.ToDictionary(pair => pair.Key, pair => pair.Value with { }))).ToArray());
    }
    public void RestoreDrafts(ProfileDraftSession session, string libraryDirectory)
    {
        RefreshUnsavedState();
        var restored = new List<ProfileTab>();
        foreach (var draft in session.Profiles)
        {
            // Keep the saved version intact; restored editors always require explicit Apply.
            var tab = new ProfileTab { DraftId = draft.Id, Name = draft.Name, RequiresApply = true };
            foreach (var (axis, editor) in draft.Editors) { tab.Editors[axis] = editor with { }; }
            if (draft.SavedFileName is { } name)
            {
                var path = System.IO.Path.Combine(libraryDirectory, name);
                var saved = ProfileTabs.FirstOrDefault(candidate => string.Equals(candidate.FilePath, path, StringComparison.OrdinalIgnoreCase));
                if (saved is not null && !saved.HasUnsavedChanges)
                {
                    tab.CopySavedStateFrom(saved);
                    ProfileTabs.Remove(saved);
                }
            }
            ProfileTabs.Add(tab); restored.Add(tab);
        }
        if (restored.Count > 0) { SwitchProfile(restored[0]); }
        HasRecoverableDrafts = false;
        RefreshUnsavedState();
        ShowMessage("Unsaved drafts recovered. Review them and Apply settings to activate; Save keeps them as profiles.");
    }

    public ProfileTab DetachSelectedProfile()
    {
        StoreDraft();
        var removed = selectedProfile ?? throw new InvalidOperationException("Select a profile first.");
        RemoveSelectedProfile();
        return removed;
    }
    public void RestoreDeletedProfile(ProfileTab tab, int index)
    {
        tab.RequiresApply = true;
        ProfileTabs.Insert(Math.Clamp(index, 0, ProfileTabs.Count), tab);
        SwitchProfile(tab);
    }

    public void RemoveSelectedProfile()
    {
        if (selectedProfile is not { } removed) { return; }
        var index = ProfileTabs.IndexOf(removed);
        var next = ProfileTabs.Skip(index + 1).Concat(ProfileTabs.Take(index).Reverse()).FirstOrDefault(CanApply);
        if (next is not null) { SwitchProfile(next); }
        else
        {
            Load(DashboardEngine.DefaultProfile());
            selectedProfile!.Recoverable = false;
            MarkSelectedSaved();
        }
        ProfileTabs.Remove(removed);

        static bool CanApply(ProfileTab tab)
        {
            try
            {
                _ = new PedalProfile(tab.Name, tab.Editors.ToDictionary(pair => pair.Key, pair => pair.Value.BuildProfile()));
                return true;
            }
            catch (ArgumentException) { return false; }
        }
    }

    private void StoreDraft()
    {
        if (selectedProfile is null) { return; }
        selectedProfile.Name = ProfileName;
        selectedProfile.Editors.Clear();
        foreach (var (axis, editor) in editors) { selectedProfile.Editors.Add(axis, editor with { }); }
    }
}
