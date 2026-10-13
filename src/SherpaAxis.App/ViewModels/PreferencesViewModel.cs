using SherpaAxis.App.Models;

namespace SherpaAxis.App.ViewModels;

public sealed class PreferencesViewModel : ObservableObject
{
    public sealed record LanguageChoice(string Code, string Name);
    public static IReadOnlyList<LanguageChoice> Languages { get; } = [new("en", "English"), new("uk", "Українська")];
    private string language = "en";
    public string Language { get => language; set => Set(ref language, Localization.Localizer.NormalizeLanguage(value)); }
    private bool minimizeOnClose, startWithWindows, startInTray, minimizeToTray, pauseGraphWhenHidden = true, saving;
    private string status = "Changes are saved automatically.";
    public bool MinimizeOnClose { get => minimizeOnClose; set => Set(ref minimizeOnClose, value); }
    public bool StartWithWindows { get => startWithWindows; set => Set(ref startWithWindows, value); }
    public bool StartInTray { get => startInTray; set => Set(ref startInTray, value); }
    public bool MinimizeToTray { get => minimizeToTray; set => Set(ref minimizeToTray, value); }
    public bool PauseGraphWhenHidden { get => pauseGraphWhenHidden; set => Set(ref pauseGraphWhenHidden, value); }
    public bool Saving { get => saving; set => Set(ref saving, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public ApplicationPreferences Snapshot() => new(1, MinimizeOnClose, StartWithWindows, StartInTray, MinimizeToTray, PauseGraphWhenHidden, Language);
    public void Load(ApplicationPreferences preferences)
    {
        Language = preferences.Language;
        MinimizeOnClose = preferences.MinimizeOnClose;
        StartWithWindows = preferences.StartWithWindows;
        StartInTray = preferences.StartInTray;
        MinimizeToTray = preferences.MinimizeToTray;
        PauseGraphWhenHidden = preferences.PauseGraphWhenHidden;
    }
}
