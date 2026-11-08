using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SherpaAxis.App.Models;
using SherpaAxis.App.Themes;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class TelemetryAppearanceTests
{
    private static readonly double[] BoundaryWidths = [360, 1000];
    [Fact]
    public Task TelemetryPanelUsesSherpaColorsSquareEdgesAndConventionalPedalColors() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-telemetry-tape-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new DashboardEngine();
            await using var main = new MainWindow(engine, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: new(folder));
            await main.InitializeForLaunchAsync(); engine.Begin(new("live-device-axis"));
            var view = main.OpenCompactView();
            for (var i = 0; i <= 500; i++)
            {
                var time = i * .02;
                var throttle = time < 1.5 ? .82 + .05 * Math.Sin(time * 8) : time < 2.3 ? .98 : time < 2.8 ? 0 : time < 8 ? 1 : time < 8.7 ? .15 : 1;
                var brake = time is > 8 and < 8.7 ? .68 * Math.Sin((time - 8) / .7 * Math.PI) : 0;
                var clutch = .48 + .13 * Math.Exp(-time / 2) + .025 * Math.Sin(time * 1.1);
                engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(time), 1,
                    new((ushort)(throttle * 16384), (int)(brake * 16777215), (ushort)(clutch * 16384))));
            }
            view.RefreshFrame(TimeSpan.FromSeconds(1));
            var surface = (FrameworkElement)view.Content;
            surface.Measure(new Size(640, 140)); surface.Arrange(new Rect(0, 0, 640, 140)); surface.UpdateLayout();
            Assert.True(view.AllowsTransparency); Assert.Equal(WindowStyle.None, view.WindowStyle);
            Assert.Null(view.Clip);
            var shell = Assert.IsType<Border>(view.FindName("OverlayShell"));
            Assert.Equal(new CornerRadius(0), shell.CornerRadius);
            Assert.Equal(AppColors.Panel, Assert.IsType<SolidColorBrush>(shell.Background).Color);
            Assert.Equal(AppColors.Border, Assert.IsType<SolidColorBrush>(shell.BorderBrush).Color);
            Assert.Equal(Color.FromRgb(64, 156, 255), TelemetryColors.Clutch.Color);
            foreach (var meter in WorkspaceSpacingTests.Descendants(view).OfType<ProgressBar>())
            {
                var card = (AxisCardViewModel)meter.DataContext;
                Assert.Equal(TelemetryColors.ForPedal(card.Axis).Color, Assert.IsType<SolidColorBrush>(meter.Foreground).Color);
                Assert.Equal(Orientation.Vertical, meter.Orientation);
            }
            Assert.NotEqual(AppColors.ForPedal(PedalAxis.Brake).Color, TelemetryColors.ForPedal(PedalAxis.Brake).Color);
            var image = new RenderTargetBitmap(640, 140, 96, 96, PixelFormats.Pbgra32);
            image.Render(surface);
            byte[] Pixel(int x, int y) { var pixel = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel; }
            Assert.InRange(Pixel(638, 1)[3], 230, 245);
            Assert.InRange(Pixel(638, 138)[3], 230, 245);
            Assert.InRange(Pixel(100, 3)[3], 230, 245);
            var stripe = Pixel(0, 70);
            Assert.True(stripe[0] > 180 && stripe[2] < 30);
            if (Environment.GetEnvironmentVariable("SHERPAAXIS_UI_CAPTURE_DIR") is { Length: > 0 } capture)
            {
                Directory.CreateDirectory(capture);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(capture, "telemetry-tape.png")); encoder.Save(file);
            }
            var settings = ((DashboardViewModel)main.DataContext).Preferences.Overlay;
            foreach (var width in BoundaryWidths)
            {
                settings.Width = width; settings.Height = 220;
                surface.Measure(new Size(width, 220)); surface.Arrange(new Rect(0, 0, width, 220)); surface.UpdateLayout();
                foreach (var label in WorkspaceSpacingTests.Descendants(view).OfType<TextBlock>().Where(text => text.FontSize == 12))
                {
                    var origin = label.TranslatePoint(new Point(0, 0), view);
                    Assert.InRange(origin.X, 8, width - label.ActualWidth - 8);
                    Assert.InRange(origin.Y, 8, 220 - label.ActualHeight - 8);
                }
            }
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });

    [Fact]
    public void MainApplicationColorsAreUnaffectedByTheTelemetryPalette()
    {
        var converter = new SherpaAxis.App.Controls.PedalBrushConverter();
        var main = (SolidColorBrush)converter.Convert(PedalAxis.Throttle, typeof(Brush), null!, CultureInfo.InvariantCulture);
        var tape = (SolidColorBrush)converter.Convert(PedalAxis.Throttle, typeof(Brush), "Telemetry", CultureInfo.InvariantCulture);
        Assert.Same(AppColors.ForPedal(PedalAxis.Throttle), main);
        Assert.Same(TelemetryColors.Throttle, tape);
    }
}
