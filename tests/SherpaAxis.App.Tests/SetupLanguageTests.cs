using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Persistence;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class SetupLanguageTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task UninstallLanguageChoiceLocalizesRemovalWithoutRunningIt(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var original = new ApplicationPreferences(StartWithWindows: true, TourDismissed: true);
        var store = new SettingsAndTrayTests.FakeStore { Value = original };
        var chooser = new SetupLanguageWindow(store, uninstall: true) { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            chooser.Show();
            await chooser.SelectLanguageAsync(language);
            Assert.True(chooser.Accepted);
            Assert.Equal(original with { Language = language }, store.Value);
            Assert.Equal(Localizer.Translate("Uninstall language"), chooser.Title);
            Assert.Equal(Localizer.Translate("Your choice will be used for uninstalling and the app."), Assert.IsType<TextBlock>(chooser.FindName("Explanation")).Text);
            var uninstall = new ZipUninstallWindow([], (_, _) => throw new InvalidOperationException("Uninstall must not run while choosing a language."));
            try
            {
                Assert.Equal(Localizer.Translate("Uninstall SherpaAxis"), uninstall.Title);
                Assert.False(uninstall.Completed);
            }
            finally { uninstall.Close(); }
        }
        finally { chooser.Close(); Localizer.Instance.Language = "en"; }
    });

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task SelectionPersistsOnlyLanguageAndLocalizesTheFollowingSetup(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var original = new ApplicationPreferences(StartWithWindows: true, StartInTray: true, TourDismissed: true, CheckForUpdates: false);
        var store = new SettingsAndTrayTests.FakeStore { Value = original };
        var window = new SetupLanguageWindow(store) { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            Assert.False(window.Accepted);
            Assert.Equal(original, store.Value);
            await window.SelectLanguageAsync(language);
            Assert.True(window.Accepted);
            Assert.Equal(original with { Language = language }, store.Value);
            Assert.Equal(language, Localizer.Instance.Language);
            var setup = new PrerequisiteSetupWindow(_ => throw new InvalidOperationException("Installer must not run in this test."));
            try
            { Assert.Equal(Localizer.Translate("Prepare your pedal drivers"), Assert.IsType<TextBlock>(setup.FindName("Heading")).Text); }
            finally { setup.Close(); }
        }
        finally { window.Close(); Localizer.Instance.Language = "en"; }
    });

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    [InlineData("en", true)]
    [InlineData("uk", true)]
    public Task ClosingTheChooserDoesNotAcceptOrChangePreferences(string language, bool uninstall = false) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        Localizer.Instance.Language = language;
        var store = new SettingsAndTrayTests.FakeStore { Value = new(Language: language) };
        var window = new SetupLanguageWindow(store, uninstall) { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            UiRefinementTests.CapturePreview((FrameworkElement)window.Content, $"setup-language-{language}.png");
            window.Close();
            Assert.False(window.Accepted);
            Assert.Equal(new(Language: language), store.Value);
            Assert.Equal(language, Localizer.Instance.Language);
        }
        finally { window.Close(); Localizer.Instance.Language = "en"; }
    });

    [Fact]
    public Task FailedSaveKeepsTheChooserOpenForRetryWithoutChangingOtherSettings() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var store = new SettingsAndTrayTests.FakeStore { Value = new(StartWithWindows: true), Fail = true };
        var window = new SetupLanguageWindow(store) { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            await window.SelectLanguageAsync("uk");
            Assert.False(window.Accepted);
            Assert.True(window.IsVisible);
            Assert.Equal(new(StartWithWindows: true), store.Value);
            Assert.True(Assert.IsType<Button>(window.FindName("UkrainianButton")).IsEnabled);
            Assert.Contains("Не вдалося зберегти", Assert.IsType<TextBlock>(window.FindName("StatusText")).Text, StringComparison.Ordinal);
            store.Fail = false;
            await window.SelectLanguageAsync("uk");
            Assert.True(window.Accepted);
            Assert.Equal(new(StartWithWindows: true, Language: "uk"), store.Value);
        }
        finally { window.Close(); Localizer.Instance.Language = "en"; }
    });

    [Fact]
    public Task WindowCannotCloseOrSubmitAnotherChoiceWhilePreferencesAreBeingSaved() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SettingsAndTrayTests.FakeStore { WriteBarrier = written.Task };
        var window = new SetupLanguageWindow(store) { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            var saving = window.SelectLanguageAsync("uk");
            window.Close();
            await window.SelectLanguageAsync("en");
            Assert.True(window.IsVisible);
            Assert.False(window.Accepted);
            Assert.False(Assert.IsType<Button>(window.FindName("CancelButton")).IsEnabled);
            written.SetResult();
            await saving;
            Assert.True(window.Accepted);
            Assert.Equal("uk", store.Value.Language);
        }
        finally { written.TrySetResult(); window.Close(); Localizer.Instance.Language = "en"; }
    });

    [Fact]
    public Task UnreadableSettingsArePreservedInsteadOfBeingReplacedWithDefaults() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "SherpaAxis-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        const string content = "{invalid settings";
        await File.WriteAllTextAsync(path, content);
        var window = new SetupLanguageWindow(new ApplicationPreferencesStore(path));
        try
        {
            await window.SelectLanguageAsync("uk");
            Assert.False(window.Accepted);
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }
        finally { window.Close(); Localizer.Instance.Language = "en"; File.Delete(path); Directory.Delete(root); }
    });
}
