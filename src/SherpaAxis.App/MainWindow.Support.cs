using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SherpaAxis.App.Services;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private async void ExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Localization.Localizer.Translate("Export diagnostic report"),
            Filter = "ZIP (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = "SherpaAxis-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".zip"
        };
        if (dialog.ShowDialog(this) == true) { await RunAsync(() => ExportDiagnosticsAsync(dialog.FileName)); }
    }
    internal Task ExportDiagnosticsAsync(string path)
    {
        var status = new Dictionary<string, string>
        {
            ["input"] = viewModel.Status,
            ["output"] = viewModel.OutputInfo,
            ["calibrationRecoveryBlocked"] = viewModel.Recovery.IsBlocked.ToString(),
            ["firmwareReviewBlocked"] = viewModel.Firmware.Blocked.ToString(),
            ["firmwareOperationRunning"] = viewModel.Firmware.Running.ToString(),
            ["language"] = viewModel.Preferences.Language
        };
        foreach (var check in viewModel.Diagnostics.Checks) { status[check.Title] = check.Detail; }
        return ExportCoreAsync();
        async Task ExportCoreAsync()
        {
            if (desktop.Log is null) { throw new InvalidOperationException("Local diagnostic logging is unavailable."); }
            await Task.Run(() => desktop.Log.Export(path, status));
            viewModel.ShowMessage("Diagnostic report exported. Review the ZIP before sharing it.");
        }
    }
}
