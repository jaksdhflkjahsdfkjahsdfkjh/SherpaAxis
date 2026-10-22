using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Input;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class UiRefinementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AppDriftButtonRequiresAHoldAndCancelsOnReleaseOrCaptureLoss(bool captureLoss) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var engine = new DashboardEngine();
        await using var main = new MainWindow(engine, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        PrepareDrift(engine, model);
        using var buttons = new ButtonStateBuffer();
        var fix = Assert.IsType<Button>(main.FindName("FixDriftButton"));
        double time = 0;
        void Samples(int count)
        {
            for (var index = 0; index < count; index++)
            {
                engine.ProcessCorrectionButtons(buttons);
                engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(time), 1, new(1000, 2900500, 1000)));
                time += .01;
            }
        }
        Samples(2);
        fix.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        Samples(50);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        if (captureLoss) { fix.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent }); }
        else { fix.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent }); }
        Samples(400);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        fix.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        Samples(400);
        Assert.Equal(-300000, engine.DriftState(PedalAxis.Brake).Offset, 5);
        fix.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        Assert.True(engine.DriftState(PedalAxis.Brake).CanSave);
    });

    private static void PrepareDrift(DashboardEngine engine, DashboardViewModel model)
    {
        model.SetCorrectionMode("By button");
        model.BindCorrection(new("keyboard", 65));
        var bounds = new PedalCalibration(new(1000, 15000), new(3200500, 4700500), new(1000, 15000));
        engine.Begin();
        engine.SetSensorCalibration(new("test-controller", bounds,
            new Dictionary<PedalAxis, HardwareCalibrationRange> { [PedalAxis.Brake] = bounds.Brake }));
    }
    internal static void CapturePreview(FrameworkElement element, string name)
    {
        if (Environment.GetEnvironmentVariable("SHERPAAXIS_UI_CAPTURE_DIR") is not { Length: > 0 } folder) { return; }
        System.IO.Directory.CreateDirectory(folder);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        { drawing.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight)); }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name));
        encoder.Save(file);
    }
}
