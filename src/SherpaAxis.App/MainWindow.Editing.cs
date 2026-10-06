using System.Windows;

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
        if (ResponseEditor.Points is not { } points) { return; }
        editingCurve = true;
        try { viewModel.EditCurve(points); }
        finally { editingCurve = false; }
    });
}
