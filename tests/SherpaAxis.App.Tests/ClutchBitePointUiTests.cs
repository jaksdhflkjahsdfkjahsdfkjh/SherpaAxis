using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class ClutchBitePointUiTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task ClutchControlsApplySettingsAndClearBindingsWithoutDirtyingForRuntimeToggles(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-bite-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services,
                profileLibrary: new(directory));
            Localizer.Instance.Language = language;
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            var panel = Assert.IsType<StackPanel>(main.FindName("ClutchBitePointPanel"));
            var enabled = Assert.IsType<CheckBox>(main.FindName("BitePointEnabledSetting"));
            var input = Assert.IsType<TextBox>(main.FindName("BitePointOutputInput"));
            var label = Assert.IsType<TextBlock>(main.FindName("BitePointBindingValue"));
            model.SelectedAxis = PedalAxis.Brake;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            model.SelectedAxis = PedalAxis.Clutch;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.False(enabled.IsEnabled);
            Assert.Equal("50", input.Text);
            model.BindBitePoint(new("keyboard", 65));
            input.Text = "42";
            input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            model.RefreshUnsavedState();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.True(enabled.IsEnabled);
            Assert.Contains("A", label.Text, StringComparison.Ordinal);
            Assert.Equal(.42, model.ValidateEditors().Profile.Axes[PedalAxis.Clutch].BitePoint!.Output);
            Assert.True(model.SelectedProfile!.HasUnsavedChanges);
            model.SelectedProfile.FilePath = Path.Combine(directory, "Launch.json");
            model.MarkSelectedSaved();
            model.SetBitePointEnabled(true);
            model.RefreshUnsavedState();
            Assert.True(model.BitePointEnabled);
            Assert.False(model.SelectedProfile.HasUnsavedChanges);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal(Localizer.Translate("Enabled · hold the button to apply the bite point"), Assert.IsType<TextBlock>(main.FindName("BitePointStatusLabel")).Text);
            input.Text = "101"; input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); model.RefreshUnsavedState();
            Assert.NotEmpty(model.ValidationMessage);
            input.Text = "42"; input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); model.RefreshUnsavedState();
            Assert.Empty(model.ValidationMessage);
            var content = Assert.IsAssignableFrom<FrameworkElement>(main.Content);
            content.Measure(new Size(1288, 862)); content.Arrange(new Rect(0, 0, 1288, 862)); content.UpdateLayout();
            UiRefinementTests.CapturePreview(panel, $"clutch-bite-point-{language}.png");
            Assert.IsType<Button>(main.FindName("ClearBitePointBindingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(model.HasBitePointBinding); Assert.False(model.BitePointEnabled);
            Assert.Equal(Localizer.Translate("No button bound"), label.Text);
            Assert.False(enabled.IsEnabled);
            model.SelectedAxis = PedalAxis.Throttle;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
        }
        finally
        {
            Localizer.Instance.Language = "en";
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    });

    [Fact]
    public void ProfileSwitchAndEditorRoundTripPreserveOnlySettings()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine) { SelectedAxis = PedalAxis.Clutch };
        model.Editor.BitePointOutput = "37";
        model.BindBitePoint(new("controller", 4, "wheel"));
        var saved = model.ValidateEditors().Profile;
        var launch = model.SelectedProfile!;
        model.Load(new("Other", DashboardEngine.DefaultProfile().Axes));
        Assert.False(model.HasBitePointBinding);
        model.SwitchProfile(launch);
        Assert.Equal("37", model.Editor.BitePointOutput);
        Assert.True(model.HasBitePointBinding);
        Assert.False(model.BitePointEnabled);
        var editor = AxisEditor.FromProfile(ProfileJson.Deserialize(ProfileJson.Serialize(saved)).Axes[PedalAxis.Clutch]);
        Assert.Equal("37", editor.BitePointOutput);
        Assert.Equal(new("controller", 4, "wheel"), editor.BitePointButton);
    }
}
