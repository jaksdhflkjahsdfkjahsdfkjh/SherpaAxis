namespace SherpaAxis.App.Models;

public static class ApplicationLinks
{
    public const string Repository = "jaksdhflkjahsdfkjahsdfkjh/SherpaAxis";
    public static Uri Issues { get; } = new($"https://github.com/{Repository}/issues");
    public static Uri LatestReleaseApi { get; } = new($"https://api.github.com/repos/{Repository}/releases/latest");
}
