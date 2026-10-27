using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SherpaAxis.App.Models;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private FrameworkElement? tourTarget;
    private SidePanelMode tourOriginalPanel;
    private bool tourOriginalCurvesExpanded;
    private bool tourNavigating;
    private IInputElement? tourPreviousFocus;
    private async void GettingStartedClick(object sender, RoutedEventArgs e) => await ShowGettingStartedAsync();

    internal async Task ShowGettingStartedAsync()
    {
        if (closing || disposed || viewModel.Tour.IsOpen) { return; }
        if (hardwareSession is not null || WantsCalibration || modeOperation is { IsCompleted: false } || viewModel.Recovery.IsBlocked || viewModel.Firmware.Blocked)
        { viewModel.ShowMessage("Finish calibration or recovery before starting the tour."); return; }
        tourOriginalPanel = viewModel.PanelMode;
        tourPreviousFocus = Keyboard.FocusedElement;
        tourOriginalCurvesExpanded = CurvesSection.IsExpanded;
        viewModel.Tour.Start();
        TourLayer.Visibility = Visibility.Visible;
        LayoutUpdated += TourLayoutUpdated;
        PreviewMouseDown += TourMouseDown;
        PreviewKeyDown += TourKeyDown;
        await ShowTourStepAsync();
        TourLayer.FocusNavigation();
    }

    private async void TourNextClick(object sender, RoutedEventArgs e)
    {
        if (tourNavigating || !viewModel.Tour.IsOpen) { return; }
        if (viewModel.Tour.IsLast) { await FinishTourAsync(); }
        else { viewModel.Tour.MoveNext(); await ShowTourStepAsync(); }
    }
    private async void TourBackClick(object sender, RoutedEventArgs e)
    {
        if (tourNavigating || !viewModel.Tour.IsOpen) { return; }
        viewModel.Tour.MoveBack();
        await ShowTourStepAsync();
    }
    private async void TourDismissClick(object sender, RoutedEventArgs e) => await FinishTourAsync();

    private async Task ShowTourStepAsync()
    {
        tourNavigating = true;
        try
        {
            await RequestPanelModeAsync(viewModel.Tour.Current.Panel);
            if (viewModel.IsSettingsMode) { SettingsSearchBox.Clear(); }
            if (!viewModel.Tour.IsOpen || closing) { return; }
            TourLayer.ResetContent();
            if (viewModel.Tour.Current.Target == nameof(CurvesSection)) { CurvesSection.IsExpanded = true; }
            tourTarget = FindName(viewModel.Tour.Current.Target) as FrameworkElement;
            DashboardRoot.UpdateLayout();
            tourTarget?.BringIntoView();
            DashboardRoot.UpdateLayout();
            PositionTour();
        }
        finally { tourNavigating = false; }
    }

    private void TourLayoutUpdated(object? sender, EventArgs e) => PositionTour();
    private void PositionTour()
    {
        if (!viewModel.Tour.IsOpen || !IsVisible) { return; }
        if (tourTarget is not { IsVisible: true } target) { TourLayer.ClearHighlight(); return; }
        var bounds = target.TransformToVisual(TourLayer).TransformBounds(new Rect(target.RenderSize));
        for (var parent = VisualTreeHelper.GetParent(target); parent is not null && parent != DashboardRoot; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is System.Windows.Controls.ScrollViewer scroll)
            { bounds.Intersect(scroll.TransformToVisual(TourLayer).TransformBounds(new Rect(scroll.RenderSize))); }
        }
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) { TourLayer.ClearHighlight(); return; }
        bounds.Inflate(5, 5);
        bounds.Intersect(new Rect(TourLayer.RenderSize));
        if (bounds.IsEmpty) { TourLayer.ClearHighlight(); return; }
        TourLayer.Place(bounds);
    }

    private void TourMouseDown(object sender, MouseButtonEventArgs e) => ObserveTourInput(e.OriginalSource as DependencyObject);
    private async void TourKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; await FinishTourAsync(); }
        else { ObserveTourInput(e.OriginalSource as DependencyObject); }
    }
    private void ObserveTourInput(DependencyObject? source)
    {
        while (source is Visual)
        {
            if (source == tourTarget) { viewModel.Tour.ObserveInteraction(); return; }
            source = VisualTreeHelper.GetParent(source);
        }
    }

    private void StopTour()
    {
        LayoutUpdated -= TourLayoutUpdated;
        PreviewMouseDown -= TourMouseDown;
        PreviewKeyDown -= TourKeyDown;
        viewModel.Tour.IsOpen = false;
        tourTarget = null;
        TourLayer.Visibility = Visibility.Collapsed;
    }

    private async Task FinishTourAsync(bool restorePanel = true)
    {
        if (!viewModel.Tour.IsOpen) { return; }
        StopTour();
        CurvesSection.IsExpanded = tourOriginalCurvesExpanded;
        if (restorePanel && !closing) { await RequestPanelModeAsync(tourOriginalPanel); }
        if (restorePanel && tourPreviousFocus is UIElement { IsVisible: true, IsEnabled: true }) { Keyboard.Focus(tourPreviousFocus); }
        tourPreviousFocus = null;
        if (!closing) { await DismissTourAsync(); }
    }

    internal Task DismissTourAsync()
    {
        viewModel.Preferences.TourDismissed = true;
        return SavePreferencesAsync();
    }
}
