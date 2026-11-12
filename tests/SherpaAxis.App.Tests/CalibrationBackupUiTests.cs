using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class CalibrationBackupUiTests
{
    private static readonly string[] BackupActions = ["BackupHardwareButton", "RestoreHardwareBackupButton"];

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task BackupActionsAreVisibleWithoutExpandingAdvancedTools(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-backup-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var desktop = new SettingsAndTrayTests.FakeDesktop();
            desktop.Store.Value = new(Language: language, TourDismissed: true);
            await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services, profileLibrary: new(directory));
            await main.InitializeForLaunchAsync();
            await main.RequestPanelModeAsync(SidePanelMode.Calibration);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var root = Assert.IsAssignableFrom<FrameworkElement>(main.Content);
            void Layout()
            { root.Measure(new Size(1288, 1000)); root.Arrange(new Rect(0, 0, 1288, 1000)); root.UpdateLayout(); }
            Layout();
            var hardware = Assert.IsType<HardwareCalibrationView>(Assert.IsType<ContentControl>(main.FindName("CalibrationHost")).Content);
            var tools = Assert.IsType<GroupBox>(hardware.FindName("CalibrationToolsGroup"));
            foreach (var name in BackupActions)
            {
                var button = Assert.IsType<Button>(hardware.FindName(name));
                Assert.True(button.ActualHeight >= 36);
                for (var parent = LogicalTreeHelper.GetParent(button); parent is not null && parent != tools; parent = LogicalTreeHelper.GetParent(parent))
                { Assert.IsNotType<Expander>(parent); }
            }
            UiRefinementTests.CapturePreview(tools, $"calibration-backups-{language}.png");
            foreach (var section in WorkspaceSpacingTests.Descendants(tools).OfType<Expander>()) { section.IsExpanded = true; }
            Layout();
            UiRefinementTests.CapturePreview(tools, $"calibration-backups-expanded-{language}.png");
        }
        finally
        {
            Localizer.Instance.Language = "en";
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    });
}
