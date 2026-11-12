using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class UserGuidanceTests
{
    private static readonly string[] DriftActions = ["FixDriftButton", "SaveCorrectedCalibrationButton"];
    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task ActionGuidanceRendersWithLiveStableAndMissingInput(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-guidance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var desktop = new SettingsAndTrayTests.FakeDesktop();
            desktop.Store.Value = new(Language: language, TourDismissed: true);
            var engine = new DashboardEngine(new FrozenClock());
            await using var main = new MainWindow(engine, desktopServices: desktop.Services, profileLibrary: new(directory));
            await main.InitializeForLaunchAsync();
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            model.SetCorrectionMode("By button");
            model.BindCorrection(new("controller", 21, "sample-wheel"));
            var root = Assert.IsAssignableFrom<FrameworkElement>(main.Content);
            void Layout()
            {
                root.Measure(new Size(1288, 1100)); root.Arrange(new Rect(0, 0, 1288, 1100)); root.UpdateLayout();
            }
            void Capture(string element, string suffix)
            {
                Layout();
                if (element == "DriftCorrectionGroup")
                {
                    foreach (var name in DriftActions)
                    {
                        var button = Assert.IsType<Button>(main.FindName(name));
                        var label = Assert.IsType<TextBlock>(button.Content);
                        var bounds = label.TransformToAncestor(button).TransformBounds(new Rect(label.RenderSize));
                        Assert.True(bounds.Left >= button.Padding.Left - 1, $"{name}: text starts outside its padding.");
                        Assert.True(bounds.Right <= button.ActualWidth - button.Padding.Right + 1, $"{name}: text overflows its button.");
                    }
                }
                UiRefinementTests.CapturePreview(Assert.IsAssignableFrom<FrameworkElement>(main.FindName(element)), $"guidance-{suffix}-{language}.png");
            }
            model.Refresh(force: true);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(model.CanFixDrift);
            Assert.Contains("sensor data", model.DriftFixHint, StringComparison.OrdinalIgnoreCase);
            Capture("DriftCorrectionGroup", "drift-unavailable");
            var bounds = new PedalCalibration(new(1000, 15000), new(3200500, 4700500), new(1000, 15000));
            engine.Begin();
            engine.SetSensorCalibration(new("test-controller", bounds,
                new Dictionary<PedalAxis, HardwareCalibrationRange> { [PedalAxis.Brake] = bounds.Brake }));
            double time = 0;
            void Samples(int count, int input = 2900500)
            {
                for (var index = 0; index < count; index++)
                {
                    engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(time), 1, new(1000, input, 1000)));
                    time += .01;
                }
                model.Refresh(force: true);
            }
            Samples(2);
            Assert.True(model.CanFixDrift);
            Assert.Equal("No drift correction to save.", model.DriftSaveHint);
            Capture("DriftCorrectionGroup", "drift-ready");
            model.FixDrift();
            Samples(56);
            Assert.InRange(model.DriftCheckProgress, .54, .56);
            Assert.True(model.ShowDriftProgress);
            Assert.Equal("Checking release…", model.FixDriftLabel);
            Assert.False(model.CanSaveCorrectedCalibration);
            Capture("DriftCorrectionGroup", "drift-checking");
            engine.CancelFixDrift(PedalAxis.Brake);
            model.Refresh(force: true);
            Assert.False(model.ShowDriftProgress);
            Assert.Equal(0, model.DriftCheckProgress);
            Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
            model.FixDrift();
            Samples(56);
            Samples(1, 2920500);
            Assert.Equal(0, model.DriftCheckProgress);
            Samples(1);
            Samples(110);
            Assert.Equal("Applying correction…", model.FixDriftLabel);
            Assert.Equal(1, model.DriftCheckProgress);
            Capture("DriftCorrectionGroup", "drift-applying");
            Samples(350);
            Assert.Equal("Hold to fix drift", model.FixDriftLabel);
            Assert.Contains("Drift fixed for this session", model.DriftStatus);
            Assert.True(model.CanSaveCorrectedCalibration);
            Capture("DriftCorrectionGroup", "drift-holding-complete");
            engine.CancelFixDrift(PedalAxis.Brake);
            model.Refresh(force: true);
            Assert.True(model.CanSaveCorrectedCalibration);
            Assert.Empty(model.DriftSaveHint);
            Capture("DriftCorrectionGroup", "drift-complete");
            Capture("CurvesSection", "curves");
            Assert.Null(main.FindName("AdvancedMappingGroup"));
            Capture("AdvancedInputMapping", "mapping-closed");
            Assert.IsType<Expander>(main.FindName("AdvancedInputMapping")).IsExpanded = true;
            Capture("AdvancedInputMapping", "mapping-open");
            model.SelectedAxis = PedalAxis.Clutch;
            model.BindBitePoint(new("controller", 4, "sample-wheel"));
            Capture("ClutchBitePointGroup", "clutch");
            await main.RequestPanelModeAsync(SidePanelMode.Settings);
            Capture("FirmwareSettingsGroup", "firmware-closed");
            Assert.Null(main.FindName("FirmwareUpdateSection"));
            Assert.Equal(Visibility.Visible, Assert.IsType<Button>(main.FindName("UpdateFirmwareButton")).Visibility);
            Capture("FirmwareSettingsGroup", "firmware-open");
            await main.RequestPanelModeAsync(SidePanelMode.Calibration);
            Layout();
            var hardware = Assert.IsType<HardwareCalibrationView>(Assert.IsType<ContentControl>(main.FindName("CalibrationHost")).Content);
            UiRefinementTests.CapturePreview(Assert.IsType<GroupBox>(hardware.FindName("CalibrationCaptureGroup")), $"guidance-capture-{language}.png");
            Assert.False(Assert.IsType<HardwarePedalViewModel>(hardware.DataContext).CanSave);
            Assert.Equal(Localizer.Translate("Cancel is available during capture."), Assert.IsType<TextBlock>(hardware.FindName("CaptureActionHint")).Text);
        }
        finally
        {
            Localizer.Instance.Language = "en";
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    });

    [Fact]
    public void DriftActionsExplainMissingCalibrationStaleInputAndBusyOperations()
    {
        var clock = new FrozenClock();
        var engine = new DashboardEngine(clock);
        var model = new DashboardViewModel(engine);
        var bounds = new PedalCalibration(new(1000, 15000), new(3200500, 4700500), new(1000, 15000));
        engine.Begin();
        engine.SetSensorCalibration(new("test-controller", bounds));
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(1000, 2900500, 1000)));
        model.Refresh();
        Assert.False(model.CanFixDrift);
        Assert.Contains("Original raw calibration is unavailable", model.DriftFixHint);
        engine.SetSensorCalibration(new("test-controller", bounds,
            new Dictionary<PedalAxis, HardwareCalibrationRange> { [PedalAxis.Brake] = bounds.Brake }));
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(10), 1, new(1000, 2900500, 1000)));
        model.Refresh();
        Assert.True(model.CanFixDrift);
        model.CanEdit = false;
        Assert.False(model.CanFixDrift);
        Assert.Equal("Wait for the current operation to finish.", model.DriftFixHint);
        Assert.Equal(model.DriftFixHint, model.DriftSaveHint);
        model.CanEdit = true;
        Assert.True(model.CanFixDrift);
        clock.Milliseconds = 1000;
        model.Refresh();
        Assert.False(model.CanFixDrift);
        Assert.Equal("Waiting for fresh pedal input", model.DriftFixHint);
        Assert.False(model.ShowDriftProgress);
    }

    private sealed class FrozenClock : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}
