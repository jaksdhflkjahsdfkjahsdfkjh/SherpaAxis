using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class DriftControlsTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task BindingCanBeClearedAndDriftActionsFitOneRowInBothLanguages(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        try
        {
            Localizer.Instance.Language = language;
            var engine = new DashboardEngine();
            await using var main = new MainWindow(engine, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            model.SetCorrectionMode("By button");
            model.BindCorrection(new("keyboard", 65));
            var bounds = new PedalCalibration(new(1000, 15000), new(3275500, 4625500), new(1000, 15000));
            engine.Begin(new("live-device-axis"));
            engine.SetSensorCalibration(new("test-controller", bounds,
                new Dictionary<PedalAxis, HardwareCalibrationRange> { [PedalAxis.Brake] = new(3200500, 4700500) }));
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(1000, 3200500, 1000)));
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(10), 1, new(1000, 3200500, 1000)));
            model.Refresh();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var content = Assert.IsAssignableFrom<FrameworkElement>(main.Content);
            content.Measure(new Size(1288, 862)); content.Arrange(new Rect(0, 0, 1288, 862)); content.UpdateLayout();
            var row = Assert.IsType<Grid>(main.FindName("DriftActionsRow"));
            var fix = Assert.IsType<Button>(main.FindName("FixDriftButton"));
            var save = Assert.IsType<Button>(main.FindName("SaveCorrectedCalibrationButton"));
            Assert.Same(row, fix.Parent); Assert.Same(row, save.Parent);
            Assert.Equal(fix.ActualHeight, save.ActualHeight, 5);
            Assert.InRange(fix.ActualWidth + save.ActualWidth + fix.Margin.Right, row.ActualWidth - 1, row.ActualWidth + 1);
            Assert.True(fix.ActualHeight >= 42);
            Assert.True(Assert.IsType<TextBlock>(fix.Content).DesiredSize.Width <= fix.ActualWidth - 16);
            Assert.True(Assert.IsType<TextBlock>(save.Content).DesiredSize.Width <= save.ActualWidth - 16);
            var label = Assert.IsType<TextBlock>(main.FindName("CorrectionBindingValue"));
            Assert.Contains("A", label.Text, StringComparison.Ordinal);
            var clear = Assert.IsType<Button>(main.FindName("ClearCorrectionBindingButton"));
            Assert.True(clear.IsEnabled);
            if (Environment.GetEnvironmentVariable("SHERPAAXIS_UI_CAPTURE_DIR") is { Length: > 0 } folder)
            {
                var panel = Assert.IsType<DockPanel>(main.FindName("TuningSidePanel"));
                var scroll = VisualTreeHelper.GetChild(panel, VisualTreeHelper.GetChildrenCount(panel) - 1) as ScrollViewer;
                scroll?.ScrollToEnd(); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(folder);
                using var file = File.Create(Path.Combine(folder, $"drift-controls-{language}.png")); encoder.Save(file);
            }
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Null(model.Editor.CorrectionButton);
            Assert.Null(model.ValidateEditors().Profile.Axes[PedalAxis.Brake].CorrectionButton);
            Assert.False(model.HasCorrectionBinding);
            Assert.False(clear.IsEnabled);
            Assert.Equal(Localizer.Translate("No button bound"), label.Text);
            Assert.False(save.IsEnabled);
        }
        finally { Localizer.Instance.Language = "en"; }
    });
}
