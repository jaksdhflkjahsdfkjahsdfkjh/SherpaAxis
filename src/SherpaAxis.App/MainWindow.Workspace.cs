using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private void InitializeWorkspace()
    {
        PropertyChangedEventManager.AddHandler(viewModel.Firmware, FirmwareSearchStateChanged, nameof(ViewModels.FirmwareUpdateViewModel.Blocked));
        RefreshSettingsSearch();
    }
    private void FirmwareSearchStateChanged(object? sender, PropertyChangedEventArgs e) => RefreshSettingsSearch();
    private void ResetWorkspaceWidth(object sender, MouseButtonEventArgs e)
    {
        WorkspaceSidebarColumn.Width = new GridLength(430);
        e.Handled = true;
    }

    private void SelectedCurvePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is not null && sender is ComboBox { SelectedItem: string preset } && preset != viewModel.SelectedCurvePreset)
        { Run(() => viewModel.SetCurvePreset(viewModel.SelectedAxis, preset)); }
    }

    private void ResetSelectedCurveClick(object sender, RoutedEventArgs e) => Run(() => viewModel.ClearCurve(viewModel.SelectedAxis));
    private void SettingsSearchChanged(object sender, TextChangedEventArgs e) => RefreshSettingsSearch();
    private void SettingsSearchLoaded(object sender, RoutedEventArgs e) => RefreshSettingsSearch();
    private void ClearSettingsSearchClick(object sender, RoutedEventArgs e) { SettingsSearchBox.Clear(); SettingsSearchBox.Focus(); }
    private void SettingsSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SettingsSearchBox.Clear(); e.Handled = true; }
    }

    private void RefreshSettingsSearch()
    {
        if (SettingsGroups is null || SettingsSearchBox is null || viewModel is null) { return; }
        var words = SettingsSearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var visible = 0;
        foreach (var group in SettingsGroups.Children.OfType<GroupBox>())
        {
            var text = string.Join(' ', SearchableSettings(group));
            var matches = words.All(word => text.Contains(word, StringComparison.CurrentCultureIgnoreCase));
            if (group.Name == "FirmwareSettingsGroup" && viewModel.Firmware.Blocked) { matches = true; }
            group.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
            if (matches) { visible++; }
        }
        ClearSettingsSearchButton.IsEnabled = SettingsSearchBox.Text.Length > 0;
        SettingsSearchEmpty.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IEnumerable<string> SearchableSettings(DependencyObject element)
    {
        if (element is GroupBox group) { yield return group.Header?.ToString() ?? ""; yield return group.Tag?.ToString() ?? ""; }
        if (element is Expander expander) { yield return expander.Header?.ToString() ?? ""; }
        if (element is TextBlock text) { yield return text.Text; }
        if (element is ContentControl { Content: string content }) { yield return content; }
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
        {
            foreach (var value in SearchableSettings(child)) { yield return value; }
        }
    }
}
