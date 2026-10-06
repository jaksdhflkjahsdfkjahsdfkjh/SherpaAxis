using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using SherpaAxis.Bridge;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private async void EnableOutputClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        engine.SuspendOutput("restarting output");
        if (bridge is not null) { await bridge.DisposeAsync(); }
        if (!uint.TryParse(viewModel.VJoyDevice, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id is < 1 or > 16)
        {
            throw new ArgumentException("vJoy device ID must be between 1 and 16.");
        }
        var path = viewModel.VJoyPath;
        bridge = new OutputBridge(() => new VJoyController(path, id));
        try { await bridge.Ready; engine.AttachOutput(bridge); }
        catch { await bridge.DisposeAsync(); throw; }
        viewModel.ShowMessage("Virtual output enabled. Bind throttle X, brake Y, clutch Z on the selected vJoy device in the game.");
    });

    private async void DisableOutputClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        engine.SuspendOutput("disabled by user");
        if (bridge is not null) { await bridge.DisposeAsync(); bridge = null; }
    });
    private void BrowseVJoyClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "vJoy SDK (vJoyInterface.dll)|vJoyInterface.dll", Title = "Select installed x64 vJoyInterface.dll" };
        if (dialog.ShowDialog(this) == true) { viewModel.VJoyPath = dialog.FileName; viewModel.Refresh(); }
    }

}
