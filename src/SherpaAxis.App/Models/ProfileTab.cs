using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed class ProfileTab : ViewModels.ObservableObject
{
    private string name = "New profile";
    public string Name { get => name; set { if (Set(ref name, value)) { Changed(nameof(DisplayName)); } } }
    public Guid DraftId { get; init; } = Guid.NewGuid();
    private string? savedName;
    private readonly Dictionary<PedalAxis, AxisEditor> savedEditors = [];
    internal bool Recoverable { get; set; } = true;
    internal bool RequiresApply { get; set; }
    private bool hasUnsavedChanges;
    public bool HasUnsavedChanges { get => hasUnsavedChanges; internal set { if (Set(ref hasUnsavedChanges, value)) { Changed(nameof(DisplayName)); } } }
    public string DisplayName => Name + (HasUnsavedChanges ? " *" : "");
    public string? FilePath { get; set; }
    private bool autoLoad;
    public bool AutoLoad { get => autoLoad; internal set => Set(ref autoLoad, value); }
    public Dictionary<PedalAxis, AxisEditor> Editors { get; } = [];
    internal void MarkSaved()
    {
        savedName = Name;
        savedEditors.Clear();
        foreach (var (axis, editor) in Editors) { savedEditors[axis] = editor with { }; }
        HasUnsavedChanges = false;
    }

    internal void RefreshUnsavedState()
    {
        var changed = Name != savedName || Editors.Count != savedEditors.Count
            || Editors.Any(pair => !savedEditors.TryGetValue(pair.Key, out var saved) || pair.Value != saved);
        HasUnsavedChanges = changed || FilePath is null && Recoverable;
        if (changed) { Recoverable = true; }
    }

    internal void CopySavedStateFrom(ProfileTab saved)
    {
        FilePath = saved.FilePath;
        savedName = saved.savedName;
        foreach (var (axis, editor) in saved.savedEditors) { savedEditors[axis] = editor with { }; }
    }

    public override string ToString() => Name;
}
