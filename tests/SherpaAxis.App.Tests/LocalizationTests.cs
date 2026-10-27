using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SherpaAxis.App.Controls;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class LocalizationTests
{
    [Fact]
    public Task SourceBindingsKeepReadableFallbackTextAndTranslateIndividualParagraphs() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        const string missing = "A readable sentence without a catalog entry.";
        var text = new TextBlock();
        try
        {
            Localizer.Instance.Language = "en";
            Assert.False(Localizer.Instance.Translations.ContainsKey(missing));
            Localizer.Bind(text, TextBlock.TextProperty, missing + "\n\nBrake");
            Assert.Equal(missing + "\n\nBrake", text.Text);
            Localizer.Instance.Language = "uk";
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal(missing + "\n\nГальмо", text.Text);
        }
        finally { Localizer.Instance.Language = "en"; }
    });

    [Fact]
    public Task SetupGuideDisplaysInstructionsAndCurrentFirmwareWorkflowInBothLanguages() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        main.Opacity = 0; main.ShowInTaskbar = false; main.Show();
        var guide = main.CreateSetupGuideWindow();
        try
        {
            Localizer.Instance.Language = "en";
            guide.Opacity = 0; guide.ShowInTaskbar = false; guide.Show();
            var document = Assert.IsType<FlowDocumentScrollViewer>(guide.Content).Document;
            string Text() => new System.Windows.Documents.TextRange(document.ContentStart, document.ContentEnd).Text;
            Assert.StartsWith("Close FEEL-VR Control", Text(), StringComparison.Ordinal);
            Assert.DoesNotContain("Install SherpaAxis", Text(), StringComparison.Ordinal);
            Assert.Contains(document.Blocks.OfType<System.Windows.Documents.Paragraph>(), paragraph => paragraph.Inlines.FirstInline is System.Windows.Documents.Bold bold && new System.Windows.Documents.TextRange(bold.ContentStart, bold.ContentEnd).Text == "X = throttle · Y = brake · Z = clutch");
            Assert.Contains("Setup includes firmware 017, ST DfuSe and its USB driver.", Text(), StringComparison.Ordinal);
            Assert.Contains("choose Flash", Text(), StringComparison.Ordinal);
            Assert.Contains("Auto waits for five consistent press/release cycles", Text(), StringComparison.Ordinal);
            Assert.DoesNotMatch(@"\AT[0-9A-F]{12}\z", Text());
            guide.UpdateLayout();
            UiRefinementTests.CapturePreview(Assert.IsType<FlowDocumentScrollViewer>(guide.Content), "setup-guide.png");
            Localizer.Instance.Language = "uk";
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal("Налаштування пристрою", guide.Title);
            Assert.StartsWith("Закрийте FEEL-VR Control", Text(), StringComparison.Ordinal);
            Assert.Contains("Інсталятор містить прошивку 017, ST DfuSe та USB-драйвер.", Text(), StringComparison.Ordinal);
            Assert.DoesNotContain("Native calibration supports", Text(), StringComparison.Ordinal);
            Assert.DoesNotContain("Close FEEL-VR Control completely", Text(), StringComparison.Ordinal);
            Assert.DoesNotContain("For drift correction", Text(), StringComparison.Ordinal);
        }
        finally { guide.Close(); Localizer.Instance.Language = "en"; }
    });

    [Fact]
    public Task LanguageSelectorChangesTheInterfaceImmediatelyPreservesProfilesAndSurvivesRestart() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        var profileDirectory = Path.Combine(Path.GetTempPath(), "SherpaAxis-localization-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services, profileLibrary: new(profileDirectory)))
            {
                await main.InitializeForLaunchAsync();
                main.Show();
                await main.RequestPanelModeAsync(SidePanelMode.Settings);
                var viewModel = Assert.IsType<DashboardViewModel>(main.DataContext);
                viewModel.Load(new PedalProfile("Throttle", DashboardEngine.DefaultProfile().Axes));
                var profile = ProfileJson.Serialize(viewModel.ValidateEditors().Profile);
                var selector = Assert.IsType<StableComboBox>(main.FindName("LanguageSelector"));
                viewModel.CanEdit = false;
                selector.SelectedValue = "uk";
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert.Equal("uk", desktop.Store.Value.Language);
                viewModel.CanEdit = true;
                Assert.Equal("uk", Localizer.Instance.Language);
                Assert.Equal("Параметри", Assert.IsType<Button>(main.FindName("SettingsModeButton")).Content);
                Assert.Equal("Калібрування", Assert.IsType<Button>(main.FindName("CalibrationModeButton")).Content);
                Assert.Equal("Виправити дрейф", Assert.IsType<TextBlock>(Assert.IsType<Button>(main.FindName("FixDriftButton")).Content).Text);
                Assert.Equal("Зберегти калібрування", Assert.IsType<TextBlock>(Assert.IsType<Button>(main.FindName("SaveCorrectedCalibrationButton")).Content).Text);
                Assert.Contains("утиліта", main.Title, StringComparison.Ordinal);
                Assert.Equal(profile, ProfileJson.Serialize(viewModel.ValidateEditors().Profile));
                Assert.Equal("Throttle", viewModel.ProfileName);
                Assert.Equal("Brake", viewModel.SelectedAxis.ToString());
                Assert.Equal("Input vs filtered", viewModel.GraphMode);
                Assert.IsType<Expander>(main.FindName("FilterDetailsExpander")).IsExpanded = true;
                main.UpdateLayout();
                var filter = Descendants(main).OfType<StableComboBox>().Single(combo => ReferenceEquals(combo.ItemsSource, DashboardViewModel.FilterKinds));
                filter.SelectedValue = "MovingAverage";
                Assert.Equal("MovingAverage", viewModel.Editor.FilterKind);
                Assert.Equal("MovingAverage", filter.SelectedValue);
                using (var tray = new ApplicationTray(main.GetTrayState))
                {
                    tray.RebuildMenu();
                    var menu = Assert.IsType<System.Windows.Forms.ContextMenuStrip>(typeof(ApplicationTray).GetField("menu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tray));
                    Assert.Contains(menu.Items.Cast<System.Windows.Forms.ToolStripItem>(), item => item.Text == "Параметри");
                    var profiles = menu.Items.Cast<System.Windows.Forms.ToolStripItem>().OfType<System.Windows.Forms.ToolStripMenuItem>().Single(item => item.Text == "Змінити профіль");
                    Assert.Contains(profiles.DropDownItems.Cast<System.Windows.Forms.ToolStripItem>(), item => item.Text == "Throttle");
                }
                selector.IsDropDownOpen = true;
                selector.IsDropDownOpen = false;
                selector.SelectedValue = "en";
                await main.SavePreferencesAsync();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert.Equal("Settings", Assert.IsType<Button>(main.FindName("SettingsModeButton")).Content);
                selector.SelectedValue = "uk";
                await main.SavePreferencesAsync();
            }
            await using var restarted = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services, profileLibrary: new(profileDirectory));
            await restarted.InitializeDesktopAsync();
            Assert.Equal("uk", Assert.IsType<DashboardViewModel>(restarted.DataContext).Preferences.Language);
            desktop.Store.Fail = true;
            Assert.IsType<DashboardViewModel>(restarted.DataContext).Preferences.Language = "en";
            await restarted.SavePreferencesAsync();
            Assert.Equal("uk", Localizer.Instance.Language);
            Assert.Equal("uk", Assert.IsType<DashboardViewModel>(restarted.DataContext).Preferences.Language);
        }
        finally
        {
            Localizer.Instance.Language = "en";
            if (Directory.Exists(profileDirectory)) { Directory.Delete(profileDirectory, true); }
        }
    });

    [Fact]
    public Task CatalogTemplatesAndUkrainianFilterImagesAreComplete() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var localizer = Localizer.Instance;
        Assert.True(localizer.Translations.Count > 350);
        Assert.Equal("Гальмо", localizer.TranslateText("Brake", "uk"));
        Assert.Equal("Brake", localizer.TranslateText("Brake", "en"));
        Assert.Equal("Застосовано 0.25 · ціль 0.50", localizer.TranslateText("Applied 0.25 · target 0.50", "uk"));
        Assert.Equal("Застосовано: My profile", localizer.TranslateText("Applied: My profile", "uk"));
        Assert.Equal("Газ   ·   Фільтр вимкнено", localizer.TranslateText("Throttle   ·   Filter off", "uk"));
        Assert.Equal("Гальмо — крива", localizer.TranslateText("Brake curve", "uk"));
        Assert.Equal("Видалено профіль: Throttle.", localizer.TranslateText("Deleted profile: Throttle.", "uk"));
        Assert.Equal("12 відліків · 500 Гц джерела · 0.5 с · з’єднання 1", localizer.TranslateText("12 reports · 500 source Hz · 0.5 s · connection 1", "uk"));
        foreach (var (english, ukrainian) in localizer.Translations)
        {
            Assert.False(string.IsNullOrWhiteSpace(ukrainian));
            var expected = Regex.Matches(english, @"\{(\d+)\}").Select(match => match.Groups[1].Value).Order().ToArray();
            var actual = Regex.Matches(ukrainian, @"\{(\d+)(?::t)?\}").Select(match => match.Groups[1].Value).Order().ToArray();
            Assert.Equal(expected, actual);
        }
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var help = new FilteringHelpWindow(main.Resources) { Opacity = 0, ShowInTaskbar = false };
        try
        {
            help.Show();
            foreach (var expander in Descendants(help).OfType<Expander>().ToArray()) { expander.IsExpanded = true; }
            help.UpdateLayout();
            localizer.Language = "uk";
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            help.UpdateLayout();
            Assert.Equal("Довідка з фільтрації", help.Title);
            var images = Descendants(help).OfType<Image>().ToArray();
            Assert.Equal(14, images.Length);
            foreach (var image in images)
            {
                var bitmap = Assert.IsType<BitmapImage>(image.Source);
                Assert.Contains("/uk/", bitmap.UriSource.AbsoluteUri, StringComparison.Ordinal);
                Assert.Equal(1280, bitmap.PixelWidth);
            }
        }
        finally { help.Close(); localizer.Language = "en"; }
    });

    [Fact]
    public async Task LanguageChangesIgnoreGraphsWhoseUiThreadHasStopped()
    {
        Window? previous = null;
        await HardwarePedalUiTests.OnUiAsync(async () =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new SignalPlot());
            panel.Children.Add(new CurveEditor());
            previous = new Window { Content = panel, ShowInTaskbar = false, Opacity = 0 };
            previous.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            previous.Close();
        });
        Assert.NotNull(previous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!previous.Dispatcher.HasShutdownFinished) { await Task.Delay(10, timeout.Token); }
        await HardwarePedalUiTests.OnUiAsync(() =>
        {
            try { Localizer.Instance.Language = "uk"; }
            finally { Localizer.Instance.Language = "en"; }
            return Task.CompletedTask;
        });
        GC.KeepAlive(previous);
    }

    [Fact]
    public async Task OlderSettingsDefaultToEnglishAndLanguageRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1,\"MinimizeOnClose\":true}");
            var store = new ApplicationPreferencesStore(path);
            var original = await store.LoadAsync(CancellationToken.None);
            Assert.Equal("en", original.Language);
            await store.SaveAsync(original with { Language = "uk" }, CancellationToken.None);
            Assert.Equal(original with { Language = "uk" }, await store.LoadAsync(CancellationToken.None));
            Assert.Equal("en", Localizer.NormalizeLanguage("unknown"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var item in Descendants(child)) { yield return item; }
        }
    }
}
