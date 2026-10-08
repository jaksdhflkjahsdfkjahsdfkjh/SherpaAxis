namespace SherpaAxis.App.Models;

public sealed record FilterChoice(string Kind, string Name)
{
    public override string ToString() => Name;
}
