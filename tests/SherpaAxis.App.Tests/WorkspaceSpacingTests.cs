using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SherpaAxis.App.Controls;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class WorkspaceSpacingTests
{
    private static readonly string[] NestedSettingsSections = ["ConnectionDetailsExpander", "SetupToolsExpander"];

    [Theory]
    [InlineData("en", 1340, 940, 430)]
    [InlineData("uk", 1340, 940, 430)]
    [InlineData("en", 1100, 760, 380)]
    [InlineData("uk", 1100, 760, 380)]
    public Task GroupsHaveContentInsetsScrollbarGuttersAndUsableBindingControls(string language, int width, int height, int sidebar) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-spacing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var desktop = new SettingsAndTrayTests.FakeDesktop();
            desktop.Store.Value = new(Language: language, TourDismissed: true);
            await using var main = new MainWindow(new(), desktopServices: desktop.Services, profileLibrary: new(directory))
            { Width = width, Height = height, WindowState = WindowState.Normal, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            await main.InitializeForLaunchAsync();
            Assert.IsType<ColumnDefinition>(main.FindName("WorkspaceSidebarColumn")).Width = new GridLength(sidebar);
            main.Show();
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            model.SetCorrectionMode("By button");
            model.BindCorrection(new("controller", 21, "sample-wheel"));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            main.UpdateLayout();

            var drift = Assert.IsType<GroupBox>(main.FindName("DriftCorrectionGroup"));
            AssertInset(drift);
            var bind = Assert.IsType<Button>(main.FindName("BindCorrectionButton"));
            var clear = Assert.IsType<Button>(main.FindName("ClearCorrectionBindingButton"));
            Assert.True(bind.ActualWidth >= 84 && bind.ActualHeight >= 40);
            Assert.Equal(40, clear.ActualWidth);
            Assert.Equal(40, clear.ActualHeight);
            Assert.True(bind.TranslatePoint(new Point(), drift).X >= 16);
            Assert.True(clear.TranslatePoint(new Point(clear.ActualWidth, 0), drift).X <= drift.ActualWidth - 16);
            UiRefinementTests.CapturePreview(drift, $"spacing-drift-{language}-{sidebar}.png");

            var mapping = Assert.IsType<Expander>(main.FindName("AdvancedInputMapping"));
            Assert.Equal(Localizer.Translate("Input range"), mapping.Header);
            Assert.Equal(0, mapping.Margin.Top);
            mapping.IsExpanded = true;
            main.UpdateLayout();
            var mappingGroup = Assert.IsType<GroupBox>(main.FindName("AdvancedMappingGroup"));
            AssertInset(mappingGroup);
            UiRefinementTests.CapturePreview(mappingGroup, $"spacing-mapping-{language}-{sidebar}.png");

            var curves = Assert.IsType<Expander>(main.FindName("CurvesSection"));
            var body = Assert.IsType<Border>(curves.Template.FindName("ExpandedBody", curves));
            Assert.Equal(Visibility.Visible, body.Visibility);
            Assert.Equal(3, Descendants(curves).OfType<CurveEditor>().Count());
            Assert.True(body.Padding.Left >= 16 && body.Padding.Right >= 16);
            UiRefinementTests.CapturePreview(curves, $"spacing-curves-{language}-{width}.png");
            curves.IsExpanded = false; main.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, body.Visibility);
            curves.IsExpanded = true;

            await main.RequestPanelModeAsync(SidePanelMode.Settings);
            main.UpdateLayout();
            var settings = Assert.IsType<ScrollViewer>(main.FindName("SettingsSidePanel"));
            var scrollBar = Assert.IsType<ScrollBar>(settings.Template.FindName("PART_VerticalScrollBar", settings));
            var groups = Assert.IsType<StackPanel>(main.FindName("SettingsGroups")).Children.OfType<GroupBox>().ToArray();
            foreach (var group in groups)
            {
                AssertInset(group);
                var right = group.TranslatePoint(new Point(group.ActualWidth, 0), settings).X;
                var barLeft = scrollBar.TranslatePoint(new Point(), settings).X;
                Assert.True(barLeft - right >= 10, $"{group.Name}: scrollbar gutter {barLeft - right:F1}");
            }
            UiRefinementTests.CapturePreview(settings, $"spacing-settings-{language}-{sidebar}.png");
            UiRefinementTests.CapturePreview((FrameworkElement)main.Content, $"spacing-window-{language}-{width}.png");

            model.Diagnostics.Checks.Add(new DeviceCheck("FEEL-VR Control", "FEEL-VR Control is closed."));
            model.Diagnostics.Checks.Add(new DeviceCheck("Firmware", "PE0 · firmware 017 · native calibration supported"));
            model.Diagnostics.Checks.Add(new DeviceCheck("vJoy driver and SDK", "Matching vJoy 2.2.2 driver and SDK are available."));
            model.Diagnostics.Checks.Add(new DeviceCheck("Pedal axes", "X = throttle · Y = brake · Z = clutch. Configuration verified automatically."));
            foreach (var name in NestedSettingsSections)
            {
                var expander = Assert.IsType<Expander>(main.FindName(name));
                expander.SetCurrentValue(Expander.IsExpandedProperty, true);
                main.UpdateLayout();
                var expanded = Assert.IsType<Border>(expander.Template.FindName("ExpandedContent", expander));
                var content = Assert.IsAssignableFrom<FrameworkElement>(expanded.Child);
                var inside = content.TransformToAncestor(expander).TransformBounds(new Rect(content.RenderSize));
                Assert.True(inside.Left >= 12, $"{name}: left inset {inside.Left:F1}");
                Assert.True(expander.ActualWidth - inside.Right >= 12, $"{name}: right inset {expander.ActualWidth - inside.Right:F1}");
                Assert.True(expanded.Padding.Top >= 10 && expanded.Padding.Bottom >= 10);
                UiRefinementTests.CapturePreview(expander, $"spacing-{name}-{language}-{sidebar}.png");
            }
            UiRefinementTests.CapturePreview(Assert.IsType<GroupBox>(main.FindName("OutputSettingsGroup")), $"spacing-output-details-{language}-{sidebar}.png");

            await main.RequestPanelModeAsync(SidePanelMode.Calibration);
            main.UpdateLayout();
            var hardware = Assert.IsType<HardwareCalibrationView>(Assert.IsType<ContentControl>(main.FindName("CalibrationHost")).Content);
            AssertInset(Assert.IsType<GroupBox>(hardware.FindName("CalibrationCaptureGroup")));
            AssertInset(Assert.IsType<GroupBox>(hardware.FindName("CalibrationRangeGroup")));
            UiRefinementTests.CapturePreview(hardware, $"spacing-calibration-{language}-{sidebar}.png");
        }
        finally
        {
            Localizer.Instance.Language = "en";
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    });

    private static void AssertInset(GroupBox group)
    {
        group.ApplyTemplate();
        var surface = Assert.IsType<Border>(group.Template.FindName("GroupContentSurface", group));
        var content = Assert.IsAssignableFrom<FrameworkElement>(surface.Child);
        var bounds = content.TransformToAncestor(group).TransformBounds(new Rect(content.RenderSize));
        Assert.True(bounds.Left >= 16, $"{group.Name}: left inset {bounds.Left:F1}");
        Assert.True(group.ActualWidth - bounds.Right >= 16, $"{group.Name}: right inset {group.ActualWidth - bounds.Right:F1}");
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }
}
