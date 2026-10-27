using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class WorkspaceUiTests
{
    [Theory]
    [InlineData("en", 1340, 940)]
    [InlineData("uk", 1340, 940)]
    [InlineData("en", 1100, 760)]
    [InlineData("uk", 1100, 760)]
    public Task GroupedTabsRenderAtSupportedSizesAndKeepTheirCachedControls(string language, int width, int height) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-workspace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var desktop = new SettingsAndTrayTests.FakeDesktop();
            desktop.Store.Value = new(Language: language, TourDismissed: true);
            var hardwareConnections = 0;
            var engine = new DashboardEngine();
            await using var main = new MainWindow(engine, (_, _, _) =>
            { hardwareConnections++; throw new InvalidOperationException("Rendering tabs must not connect hardware."); }, desktop.Services, new(directory))
            { Width = width, Height = height, WindowState = WindowState.Normal, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            await main.InitializeForLaunchAsync();
            main.Show();
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            engine.Begin();
            for (var index = 0; index < 160; index++)
            {
                var wave = Math.Pow(Math.Sin(index / 28d), 2);
                engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(index * 40), 1,
                    new((int)(wave * 12000), (int)(wave * 12000000), (int)((1 - wave) * 12000))));
            }
            model.SourceName = "Sample pedal input";
            model.Refresh(force: true);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            main.UpdateLayout();
            var root = Assert.IsAssignableFrom<FrameworkElement>(main.Content);
            var response = Assert.IsType<GroupBox>(main.FindName("ResponseSettingsGroup"));
            Assert.True(response.ActualWidth > 300);
            var bounds = response.TransformToAncestor(root).TransformBounds(new Rect(response.RenderSize));
            Assert.True(bounds.Right <= root.ActualWidth + 1);
            var column = Assert.IsType<ColumnDefinition>(main.FindName("WorkspaceSidebarColumn"));
            column.Width = new GridLength(480);
            Assert.IsType<GridSplitter>(main.FindName("WorkspaceSplitter")).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Control.MouseDoubleClickEvent });
            Assert.Equal(430, column.Width.Value);
            main.UpdateLayout();
            Assert.True(Assert.IsType<Button>(main.FindName("SaveTuningProfileButton")).IsVisible);
            Assert.Equal(Localizer.Translate("Response and deadzones"), response.Header);
            UiRefinementTests.CapturePreview(root, $"workspace-tuning-{language}-{width}.png");
            var curveSelector = main.FindName("SelectedCurvePresetSelector");
            await main.RequestPanelModeAsync(SidePanelMode.Calibration);
            main.UpdateLayout();
            var host = Assert.IsType<ContentControl>(main.FindName("CalibrationHost"));
            var view = Assert.IsType<HardwareCalibrationView>(host.Content);
            var hardware = Assert.IsType<HardwarePedalViewModel>(view.DataContext);
            Assert.False(hardware.HasSession || hardware.CanSave);
            Assert.True(hardware.CanEditMargins);
            Assert.Equal(Localizer.Translate("1. Capture pedal range"), Assert.IsType<GroupBox>(view.FindName("CalibrationCaptureGroup")).Header);
            UiRefinementTests.CapturePreview(root, $"workspace-calibration-{language}-{width}.png");
            await main.RequestPanelModeAsync(SidePanelMode.Settings);
            main.UpdateLayout();
            var groups = Assert.IsType<StackPanel>(main.FindName("SettingsGroups")).Children.OfType<GroupBox>().ToArray();
            Assert.Equal(6, groups.Length);
            Assert.All(groups, group => Assert.True(group.ActualWidth > 300));
            UiRefinementTests.CapturePreview(root, $"workspace-settings-{language}-{width}.png");
            await main.RequestPanelModeAsync(SidePanelMode.Tuning);
            Assert.Same(curveSelector, main.FindName("SelectedCurvePresetSelector"));
            await main.RequestPanelModeAsync(SidePanelMode.Calibration);
            Assert.Same(view, host.Content);
            Assert.Equal(0, hardwareConnections);
        }
        finally { Localizer.Instance.Language = "en"; if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });

    [Theory]
    [InlineData("en", "firmware")]
    [InlineData("uk", "прошивка")]
    public Task SettingsSearchKeepsEditorStateAndNeverHidesPendingFirmwareRecovery(string language, string query) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-search-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var main = new MainWindow(new(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: new(directory));
            Localizer.Instance.Language = language;
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            model.Editor.LowerDeadzone = "7";
            model.RefreshUnsavedState();
            var profile = ProfileJson.Serialize(model.ValidateEditors().Profile);
            var preferences = model.Preferences.Snapshot();
            await main.RequestPanelModeAsync(SidePanelMode.Settings);
            var search = Assert.IsType<TextBox>(main.FindName("SettingsSearchBox"));
            search.Text = query;
            var groups = Assert.IsType<StackPanel>(main.FindName("SettingsGroups")).Children.OfType<GroupBox>().ToArray();
            Assert.Equal("FirmwareSettingsGroup", Assert.Single(groups, group => group.Visibility == Visibility.Visible).Name);
            search.Text = "zzzz-not-a-setting";
            Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(main.FindName("SettingsSearchEmpty")).Visibility);
            model.Firmware.Blocked = true;
            Assert.Equal(Visibility.Visible, Assert.IsType<GroupBox>(main.FindName("FirmwareSettingsGroup")).Visibility);
            model.Firmware.Blocked = false;
            Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(main.FindName("SettingsSearchEmpty")).Visibility);
            Assert.IsType<Button>(main.FindName("ClearSettingsSearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("", search.Text);
            Assert.All(groups, group => Assert.Equal(Visibility.Visible, group.Visibility));
            Assert.Equal(profile, ProfileJson.Serialize(model.ValidateEditors().Profile));
            Assert.Equal(preferences, model.Preferences.Snapshot());
        }
        finally { Localizer.Instance.Language = "en"; if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });

    [Fact]
    public Task ResponseShortcutsStayInSyncAndPreserveOtherPedalSettings() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-response-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var main = new MainWindow(new(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: new(directory));
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            model.SelectedAxis = PedalAxis.Brake;
            model.SetFilterPreset("Balanced");
            model.Editor.LowerDeadzone = "7";
            model.RefreshUnsavedState();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var preset = Assert.IsAssignableFrom<ComboBox>(main.FindName("SelectedCurvePresetSelector"));
            preset.SetCurrentValue(ComboBox.SelectedItemProperty, "Progressive");
            Assert.NotNull(preset.GetBindingExpression(ComboBox.SelectedItemProperty));
            Assert.Equal("Progressive", model.SelectedCurvePreset);
            Assert.IsType<Button>(main.FindName("ResetSelectedCurveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Linear", model.SelectedCurvePreset);
            Assert.Equal("7", model.Editor.LowerDeadzone);
            Assert.Equal("Balanced", model.FilterPreset);
            model.SelectedAxis = PedalAxis.Clutch;
            Assert.Equal(PedalAxis.Clutch, model.SelectedAxis);
            model.SetCurvePreset(PedalAxis.Clutch, "S curve");
            Assert.Equal("S curve", model.SelectedCurvePreset);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal("S curve", preset.SelectedItem);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });

    [Fact]
    public void FirmwareRecoveryIsShownAsPausedRecoveryInsteadOfDisconnectedHardware()
    {
        var model = new DashboardViewModel(new());
        model.Firmware.Blocked = true;
        model.Refresh(force: true);
        Assert.Equal("Firmware recovery required", model.Status);
        model.Firmware.Running = true;
        model.Refresh(force: true);
        Assert.Equal("Updating firmware", model.Status);
    }
}
