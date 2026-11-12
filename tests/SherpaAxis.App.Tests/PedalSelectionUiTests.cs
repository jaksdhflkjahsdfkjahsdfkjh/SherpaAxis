using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class PedalSelectionUiTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task SelectionStaysInSyncAcrossCardsListsAndCalibration(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var desktop = new SettingsAndTrayTests.FakeDesktop();
            desktop.Store.Value = new(Language: language, TourDismissed: true);
            await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services, profileLibrary: new(directory));
            await main.InitializeForLaunchAsync();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            var root = Assert.IsAssignableFrom<FrameworkElement>(main.Content);
            void Layout()
            { root.Measure(new Size(1288, 1000)); root.Arrange(new Rect(0, 0, 1288, 1000)); root.UpdateLayout(); }
            Layout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Layout();
            var cards = WorkspaceSpacingTests.Descendants(root).OfType<ItemsControl>().Single(items => ReferenceEquals(items.ItemsSource, model.Cards));
            var brake = WorkspaceSpacingTests.Descendants(cards).OfType<Button>().Single(button => Equals(button.Tag, PedalAxis.Brake));
            var save = Assert.IsType<Button>(main.FindName("SaveTuningProfileButton"));
            var heading = Assert.IsType<StackPanel>(Assert.IsType<Grid>(save.Parent).Parent);
            var title = Assert.IsType<TextBlock>(main.FindName("SelectedPedalHeading"));
            Assert.Equal(PedalAxis.Brake, Assert.Single(model.Cards, card => card.IsSelected).Axis);
            Assert.Equal(Localizer.Translate("Brake settings"), title.Text);
            Assert.Same(main.FindResource("AccentSoftBrush"), brake.Background);
            UiRefinementTests.CapturePreview(cards, $"selection-cards-{language}.png");
            UiRefinementTests.CapturePreview(brake, $"selection-card-{language}.png");
            UiRefinementTests.CapturePreview(heading, $"selection-heading-{language}.png");
            var throttle = WorkspaceSpacingTests.Descendants(cards).OfType<Button>().Single(button => Equals(button.Tag, PedalAxis.Throttle));
            throttle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout();
            Assert.Equal(PedalAxis.Throttle, model.SelectedAxis);
            Assert.Equal(PedalAxis.Throttle, Assert.Single(model.Cards, card => card.IsSelected).Axis);
            Assert.Equal(Localizer.Translate("Throttle settings"), title.Text);
            var selector = WorkspaceSpacingTests.Descendants(root).OfType<ComboBox>().Single(combo =>
                BindingOperations.GetBinding(combo, ComboBox.SelectedItemProperty)?.Path.Path == "SelectedAxis");
            selector.SelectedItem = PedalAxis.Clutch;
            Layout();
            Assert.Equal(PedalAxis.Clutch, model.SelectedAxis);
            Assert.Equal(PedalAxis.Clutch, Assert.Single(model.Cards, card => card.IsSelected).Axis);
            Assert.Equal(Localizer.Translate("Clutch settings"), title.Text);
            await main.RequestPanelModeAsync(SidePanelMode.Calibration);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Layout();
            var hardware = Assert.IsType<HardwareCalibrationView>(Assert.IsType<ContentControl>(main.FindName("CalibrationHost")).Content);
            var device = Assert.IsType<HardwarePedalViewModel>(hardware.DataContext);
            device.SelectedChannel = PedalChannel.Throttle;
            Layout();
            Assert.Equal(PedalAxis.Throttle, Assert.Single(model.Cards, card => card.IsSelected).Axis);
            device.Busy = true;
            Layout();
            Assert.All(WorkspaceSpacingTests.Descendants(cards).OfType<Button>(), button => Assert.False(button.IsEnabled));
            brake.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(PedalAxis.Throttle, model.SelectedAxis);
            Assert.Equal(PedalChannel.Throttle, device.SelectedChannel);
            device.Busy = false;
            Layout();
            Assert.All(WorkspaceSpacingTests.Descendants(cards).OfType<Button>(), button => Assert.True(button.IsEnabled));
        }
        finally
        {
            Localizer.Instance.Language = "en";
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    });
}
