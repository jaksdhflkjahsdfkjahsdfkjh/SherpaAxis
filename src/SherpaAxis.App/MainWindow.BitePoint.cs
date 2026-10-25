using System.Windows;
using System.Windows.Controls;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private void BindBitePointClick(object sender, RoutedEventArgs e)
    {
        var dialog = new BindButtonDialog(this, buttons, "Bind clutch bite point",
            "Double-tap to enable or disable. Hold the same button to use the selected clutch mode.");
        if (dialog.ShowDialog() == true && dialog.Binding is { } binding) { Run(() => viewModel.BindBitePoint(binding)); }
    }
    private void ClearBitePointBindingClick(object sender, RoutedEventArgs e) => Run(viewModel.ClearBitePointBinding);
    private void BitePointEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkbox) { viewModel.SetBitePointEnabled(checkbox.IsChecked == true); }
    }
    private void BitePointModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is not null && sender is ComboBox { SelectedItem: string mode } && mode != viewModel.SelectedBitePointMode)
        { Run(() => viewModel.SetBitePointMode(mode)); }
    }
}
