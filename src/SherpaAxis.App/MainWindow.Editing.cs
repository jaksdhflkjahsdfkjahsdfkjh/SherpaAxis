using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SherpaAxis.App.Controls;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private bool curveRefreshPending;
    private bool filterRefreshPending;

    private void CurveSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (viewModel is not null && !editingCurve && !curveRefreshPending)
        {
            curveRefreshPending = true;
            Dispatcher.BeginInvoke(() => { curveRefreshPending = false; if (!editingCurve && !closing) { viewModel.RefreshDraftCurve(); } });
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

    private void FilterValuesChanged(object sender, RoutedEventArgs e)
    {
        if (viewModel is null || filterRefreshPending || closing) { return; }
        filterRefreshPending = true;
        Dispatcher.BeginInvoke(() => { filterRefreshPending = false; if (!closing) { viewModel.NotifyFilterSettings(); } });
    }

    private void ParameterFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (viewModel is not null && e.NewFocus is FrameworkElement { ToolTip: string hint }) { viewModel.ContextHint = hint; }
    }
}
