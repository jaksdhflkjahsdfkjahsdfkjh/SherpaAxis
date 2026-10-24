using System.Windows;
using System.Windows.Controls;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private void BindBitePointClick(object sender, RoutedEventArgs e)
    {
        var dialog = new BindButtonDialog(this, buttons, "Bind clutch bite point",
            "Double-tap to enable or disable. While enabled, hold the same button to apply the bite point.");
        if (dialog.ShowDialog() == true && dialog.Binding is { } binding) { Run(() => viewModel.BindBitePoint(binding)); }
    }
    private void ClearBitePointBindingClick(object sender, RoutedEventArgs e) => Run(viewModel.ClearBitePointBinding);
    private void BitePointEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkbox) { viewModel.SetBitePointEnabled(checkbox.IsChecked == true); }
    }
}
