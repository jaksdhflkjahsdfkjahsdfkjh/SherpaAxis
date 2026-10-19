using System.ComponentModel;
using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class NativeErrorHandlingTests
{
    [Fact]
    public Task AWindowsDeviceErrorKeepsTheCalibrationControlsUsable() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var main = new MainWindow(new DashboardEngine(),
            (_, _, _) => throw new Win32Exception(5, "Simulated native access failure"), new SettingsAndTrayTests.FakeDesktop().Services);
        await main.RequestPanelModeAsync(SidePanelMode.Calibration);
        var model = (DashboardViewModel)main.DataContext;
        Assert.True(model.CanEdit);
        Assert.False(model.IsSwitchingMode);
        Assert.Equal("Simulated native access failure", model.Message);
    });
}
