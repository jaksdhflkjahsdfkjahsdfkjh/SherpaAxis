using System.Windows;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private void FixDriftClick(object sender, RoutedEventArgs e) => Run(viewModel.FixDrift);
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
