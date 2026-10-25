using System.Windows;
using System.Windows.Controls;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Persistence;

namespace SherpaAxis.App;

public partial class SetupLanguageWindow : Window
{
    private readonly IApplicationPreferencesStore preferences;
    private readonly string initialLanguage = Localizer.Instance.Language;
    private bool saving;
    internal bool Accepted { get; private set; }

    public SetupLanguageWindow(bool uninstall = false) : this(new ApplicationPreferencesStore(ApplicationPreferencesStore.DefaultPath), uninstall) { }
    internal SetupLanguageWindow(IApplicationPreferencesStore preferences, bool uninstall = false)
    {
        this.preferences = preferences;
        InitializeComponent();
        Localizer.Bind(this, TitleProperty, uninstall ? "Uninstall language" : "Setup language");
        Localizer.Bind(Heading, TextBlock.TextProperty, "Choose your language");
        Localizer.Bind(Explanation, TextBlock.TextProperty, uninstall ? "Your choice will be used for uninstalling and the app." : "Your choice will be used for setup and the app.");
        Localizer.Bind(CancelButton, ContentControl.ContentProperty, "Cancel");
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        Loaded += (_, _) => (initialLanguage == "uk" ? UkrainianButton : EnglishButton).Focus();
        Closing += (_, args) => args.Cancel = saving;
        Closed += (_, _) => { if (!Accepted) { Localizer.Instance.Language = initialLanguage; } };
    }

    private async void EnglishClick(object sender, RoutedEventArgs e) => await SelectLanguageAsync("en");
    private async void UkrainianClick(object sender, RoutedEventArgs e) => await SelectLanguageAsync("uk");
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    internal async Task SelectLanguageAsync(string language)
    {
        if (saving) { return; }
        saving = true;
        EnglishButton.IsEnabled = UkrainianButton.IsEnabled = CancelButton.IsEnabled = false;
        Localizer.Instance.Language = language;
        try
        {
            var current = await preferences.LoadAsync(CancellationToken.None);
            await preferences.SaveAsync(current with { Language = Localizer.Instance.Language }, CancellationToken.None);
            Accepted = true;
            saving = false;
            Close();
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        {
            DiagnosticLog.Default.Write("prerequisite-setup", "Could not save the setup language.", failure);
            Localizer.Bind(StatusText, TextBlock.TextProperty, $"Could not save the setup language. {failure.Message}");
        }
        finally { saving = false; EnglishButton.IsEnabled = UkrainianButton.IsEnabled = CancelButton.IsEnabled = true; }
    }
}
