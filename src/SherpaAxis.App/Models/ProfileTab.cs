namespace SherpaAxis.App.Models;

public sealed class ProfileTab : ViewModels.ObservableObject
{
    private string name = "New profile";
    public string Name { get => name; set { if (Set(ref name, value)) { Changed(nameof(DisplayName)); } } }
    public Guid DraftId { get; init; } = Guid.NewGuid();
    internal string? SavedName { get; set; }
    internal Dictionary<Profiles.PedalAxis, AxisEditor> SavedEditors { get; } = [];
    internal bool Recoverable { get; set; } = true;
    internal bool RequiresApply { get; set; }
    private bool hasUnsavedChanges;
    public bool HasUnsavedChanges { get => hasUnsavedChanges; internal set { if (Set(ref hasUnsavedChanges, value)) { Changed(nameof(DisplayName)); } } }
    public string DisplayName => Name + (HasUnsavedChanges ? " *" : "");
    public string? FilePath { get; set; }
    private bool autoLoad;
    public bool AutoLoad { get => autoLoad; internal set => Set(ref autoLoad, value); }
    public Dictionary<Profiles.PedalAxis, AxisEditor> Editors { get; } = [];
    public override string ToString() => Name;
}
