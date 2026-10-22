using System.Windows;
using System.Windows.Input;
using SherpaAxis.Profiles;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private PedalAxis? heldDriftAxis;
    private void FixDriftMouseDown(object sender, MouseButtonEventArgs e) => BeginDriftHold();
    private void FixDriftMouseUp(object sender, MouseButtonEventArgs e) => EndDriftHold();
    private void FixDriftCaptureLost(object sender, MouseEventArgs e) => EndDriftHold();
    private void FixDriftFocusLost(object sender, KeyboardFocusChangedEventArgs e) => EndDriftHold();
    private void FixDriftKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) { return; }
        e.Handled = true;
        if (!e.IsRepeat) { BeginDriftHold(); }
    }
    private void FixDriftKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter) { EndDriftHold(); e.Handled = true; }
    }
    private void BeginDriftHold() => Run(() =>
    {
        if (!viewModel.CanTune || heldDriftAxis is not null) { return; }
        viewModel.FixDrift();
        heldDriftAxis = viewModel.SelectedAxis;
    });
    private void EndDriftHold()
    {
        if (heldDriftAxis is not { } axis) { return; }
        heldDriftAxis = null;
        engine.CancelFixDrift(axis);
        viewModel.Refresh();
    }
    private async void SaveCorrectedCalibrationClick(object sender, RoutedEventArgs e) => await SaveCorrectedCalibrationAsync();

    internal Task SaveCorrectedCalibrationAsync() => RunAsync(async () =>
    {
        var correction = engine.CorrectedHardwareCalibration(viewModel.SelectedAxis);
        var save = desktop.SaveCorrectedCalibration ?? throw new InvalidOperationException("Controller calibration saving is unavailable.");
        await PrepareHardwareAsync();
        try
        {
            await save(correction, lifetime.Token);
            viewModel.ShowMessage("Corrected calibration saved to the controller. The original calibration is retained in a hardware backup.");
        }
        finally
        {
            await CheckStartupRecoveryAsync();
            await ResumeLiveAfterRecoveryAsync();
        }
    });
}
