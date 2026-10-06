namespace SherpaAxis.App.Models;

public sealed class ProfileTab : ViewModels.ObservableObject
{
    private string name = "New profile";
    public string Name { get => name; set { name = value; Changed(); } }
    public string? FilePath { get; set; }
    public Dictionary<Profiles.PedalAxis, AxisEditor> Editors { get; } = [];
    public override string ToString() => Name;
}
