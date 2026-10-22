using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
}
