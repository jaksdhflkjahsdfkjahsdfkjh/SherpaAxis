using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SherpaAxis.App.Controls;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private bool settingsRefreshPending;
    private void CurveEdited(object? sender, EventArgs e) => Run(() =>
    {
        if (sender is not CurveEditor { DataContext: PedalCurveViewModel panel, Points: { } points }) { return; }
        editingCurve = true;
        try { SelectCurvePedal(panel); viewModel.EditCurve(panel.Axis, points); }
        finally { editingCurve = false; }
    });

    private void ClearCurveClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (sender is Button { DataContext: PedalCurveViewModel panel }) { SelectCurvePedal(panel); viewModel.ClearCurve(panel.Axis); }
    });

    private void PedalPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is not null && !editingCurve && sender is ComboBox { DataContext: PedalCurveViewModel panel, SelectedItem: string name }
            && panel.CurveKind != name)
        {
            Run(() => { SelectCurvePedal(panel); viewModel.SetCurvePreset(panel.Axis, name); });
        }
    }

    private void SelectCurvePedal(PedalCurveViewModel panel)
    {
        StopHold();
        if (viewModel.SelectedAxis != panel.Axis) { viewModel.SelectedAxis = panel.Axis; }
    }

    private void FilterPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is not null && sender is ComboBox { SelectedItem: string name } && name != viewModel.FilterPreset)
        {
            Run(() => viewModel.SetFilterPreset(name));
        }
    }

    private void FilterKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is not null && sender is ComboBox { SelectedValue: string kind } && kind != viewModel.SelectedFilterKind)
        {
            Run(() => viewModel.SetFilterKind(kind));
        }
    }

    private void CorrectionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is not null && sender is ComboBox { SelectedItem: string mode } && mode != viewModel.SelectedCorrectionMode)
        { Run(() => viewModel.SetCorrectionMode(mode)); }
    }
    private void BindCorrectionClick(object sender, RoutedEventArgs e)
    {
        var dialog = new BindButtonDialog(this, buttons);
        if (dialog.ShowDialog() == true && dialog.Binding is { } button) { Run(() => viewModel.BindCorrection(button)); }
    }

    private void TuningValuesChanged(object sender, RoutedEventArgs e)
    {
        if (viewModel is null || settingsRefreshPending || closing) { return; }
        settingsRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            settingsRefreshPending = false;
            if (!closing) { viewModel.RefreshUnsavedState(); viewModel.NotifyFilterSettings(); }
        });
    }

    private void ToggleGraphPauseClick(object sender, RoutedEventArgs e) => viewModel.GraphPaused = !viewModel.GraphPaused;
    private void GraphWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox input)
        {
            input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
    private void GraphModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is not null && !viewModel.IsCalibrationMode && sender is ComboBox { SelectedItem: string mode }) { viewModel.GraphMode = mode; }
    }
    private void ResetGraphZoomClick(object sender, RoutedEventArgs e) => MainSignalPlot.ResetZoom();
    private void GraphInspectionStarted(object? sender, EventArgs e) => viewModel.GraphPaused = true;
    private void FilterHelpClick(object sender, RoutedEventArgs e)
    {
        StopHold();
        new FilteringHelpWindow(Resources) { Owner = this }.ShowDialog();
    }
}
