using System.Windows;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private CompactPedalWindow? compactView;
    private void CompactViewClick(object sender, RoutedEventArgs e) => ToggleCompactView();
    private void ToggleCompactView() => Run(() => { if (compactView is null) { OpenCompactView(); } else { CloseCompactView(); } });
    private void ShowOverlayClick(object sender, RoutedEventArgs e) => Run(() => OpenCompactView());
    private void HideOverlayClick(object sender, RoutedEventArgs e) => Run(CloseCompactView);

    internal CompactPedalWindow OpenCompactView()
    {
        if (closing || disposed) { throw new InvalidOperationException("The application is closing."); }
        if (compactView is null)
        {
            compactView = new(engine, CompactPauseReason, viewModel.Preferences.Overlay);
            compactView.Closed += (_, _) => { compactView = null; viewModel.Preferences.Overlay.IsVisible = false; };
        }
        if (compactView.WindowState == WindowState.Minimized) { compactView.WindowState = WindowState.Normal; }
        compactView.Show();
        viewModel.Preferences.Overlay.IsVisible = true;
        return compactView;
    }

    private string? CompactPauseReason() => closing ? "Closing…"
        : viewModel.Firmware.Running || viewModel.Firmware.Blocked ? "Firmware · output paused"
        : viewModel.Recovery.IsBlocked ? "Recovery · output paused"
        : hardwareSession is not null || viewModel.IsCalibrating ? "Calibration · output paused"
        : runtime?.IsPaused == true ? "Output paused"
        : null;

    private void CloseCompactView() { compactView?.Close(); compactView = null; viewModel.Preferences.Overlay.IsVisible = false; }
}
