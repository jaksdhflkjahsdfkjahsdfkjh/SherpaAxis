using System.Windows;
using System.Windows.Controls;
using SherpaAxis.App.Controls;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private void CurveSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (viewModel is not null && !editingCurve)
        {
            Dispatcher.BeginInvoke(() => { if (!editingCurve && !closing) { viewModel.RefreshDraftCurve(); } });
        }
    }
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
            && panel.Editor.CurveKind != name)
        {
            Run(() => { SelectCurvePedal(panel); viewModel.SetCurvePreset(panel.Axis, name); });
        }
    }

    private void SelectCurvePedal(PedalCurveViewModel panel)
    {
        StopHold();
        if (viewModel.SelectedAxis != panel.Axis) { viewModel.SelectedAxis = panel.Axis; }
    }
}
