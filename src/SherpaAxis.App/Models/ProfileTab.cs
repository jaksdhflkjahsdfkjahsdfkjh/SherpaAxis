using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed class ProfileTab : ViewModels.ObservableObject
{
    private string name = "New profile";
    public string Name { get => name; set { if (Set(ref name, value)) { Changed(nameof(DisplayName)); } } }
    public Guid DraftId { get; init; } = Guid.NewGuid();
    private byte[]? savedProfile;
    private byte[]? assessedSavedProfile;
    private string? assessedName;
    private readonly Dictionary<PedalAxis, AxisEditor> assessedEditors = [];
    private bool matchesSaved;
    internal bool Recoverable { get; set; } = true;
    private bool hasUnsavedChanges;
    public bool HasUnsavedChanges { get => hasUnsavedChanges; internal set { if (Set(ref hasUnsavedChanges, value)) { Changed(nameof(DisplayName)); } } }
    public string DisplayName => Name + (HasUnsavedChanges ? " *" : "");
    public string? FilePath { get; set; }
    private bool autoLoad;
    public bool AutoLoad { get => autoLoad; internal set => Set(ref autoLoad, value); }
    public Dictionary<PedalAxis, AxisEditor> Editors { get; } = [];
    internal void MarkSaved()
    {
        savedProfile = SerializeEditors();
        RefreshUnsavedState();
    }

    internal void RefreshUnsavedState()
    {
        if (!ReferenceEquals(assessedSavedProfile, savedProfile) || assessedName != Name || Editors.Count != assessedEditors.Count
            || Editors.Any(pair => !assessedEditors.TryGetValue(pair.Key, out var previous) || pair.Value != previous))
        {
            matchesSaved = false;
            try { matchesSaved = savedProfile is not null && SerializeEditors().AsSpan().SequenceEqual(savedProfile); }
            catch (ArgumentException) { }
            assessedSavedProfile = savedProfile;
            assessedName = Name;
            assessedEditors.Clear();
            foreach (var (axis, editor) in Editors) { assessedEditors[axis] = editor with { }; }
        }
        HasUnsavedChanges = !matchesSaved || FilePath is null && Recoverable;
        if (!matchesSaved) { Recoverable = true; }
    }

    internal void CopySavedStateFrom(ProfileTab saved)
    {
        FilePath = saved.FilePath;
        savedProfile = saved.savedProfile;
    }

    private byte[] SerializeEditors() => ProfileJson.Serialize(new(Name,
        Editors.ToDictionary(pair => pair.Key, pair => pair.Value.BuildProfile())));

    public override string ToString() => Name;
}
