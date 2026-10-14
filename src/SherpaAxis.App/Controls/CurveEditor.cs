using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SherpaAxis.App.Models;
using SherpaAxis.Core;

namespace SherpaAxis.App.Controls;

public sealed class CurveEditor : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points),
        typeof(IReadOnlyList<CurvePoint>), typeof(CurveEditor), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));
    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(nameof(Preview),
        typeof(IReadOnlyList<PlotSample>), typeof(CurveEditor), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AppliedProperty = DependencyProperty.Register(nameof(Applied),
        typeof(IReadOnlyList<PlotSample>), typeof(CurveEditor), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private int selected = -1;
    public CurveEditor()
    {
        Focusable = true; Cursor = Cursors.Cross;
        PropertyChangedEventManager.AddHandler(Localization.Localizer.Instance, (_, _) =>
        {
            if (Dispatcher.CheckAccess()) { InvalidateVisual(); }
            else if (!Dispatcher.HasShutdownStarted) { _ = Dispatcher.InvokeAsync(InvalidateVisual); }
        }, nameof(Localization.Localizer.Language));
    }
    public IReadOnlyList<CurvePoint>? Points { get => (IReadOnlyList<CurvePoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IReadOnlyList<PlotSample>? Preview { get => (IReadOnlyList<PlotSample>?)GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public IReadOnlyList<PlotSample>? Applied { get => (IReadOnlyList<PlotSample>?)GetValue(AppliedProperty); set => SetValue(AppliedProperty, value); }
    public event EventHandler? Edited;
    private Rect Plot => new(28, 8, Math.Max(1, ActualWidth - 40), Math.Max(1, ActualHeight - 30));
    private Point Screen(CurvePoint p) => new(Plot.Left + p.Input * Plot.Width, Plot.Bottom - p.Output * Plot.Height);
    private CurvePoint Fraction(Point p) => new(Math.Clamp((p.X - Plot.Left) / Plot.Width, 0, 1), Math.Clamp((Plot.Bottom - p.Y) / Plot.Height, 0, 1));

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(48, 65, 87)), 1);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(69, 214, 187)), 2);
        for (var i = 0; i <= 4; i++)
        {
            var x = Plot.Left + Plot.Width * i / 4;
            var y = Plot.Bottom - Plot.Height * i / 4;
            drawingContext.DrawLine(grid, new(x, Plot.Top), new(x, Plot.Bottom));
            drawingContext.DrawLine(grid, new(Plot.Left, y), new(Plot.Right, y));
        }
        DrawCurve(drawingContext, Applied, new Pen(Brushes.LightSlateGray, 1) { DashStyle = DashStyles.Dash });
        if (Points is not { Count: >= 2 } points) { return; }
        DrawCurve(drawingContext, Preview, pen);
        for (var i = 0; i < points.Count; i++)
        {
            drawingContext.DrawEllipse(i == selected ? Brushes.White : pen.Brush, grid, Screen(points[i]), 5, 5);
        }
        var text = new FormattedText(Localization.Localizer.Translate("INPUT →"), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.LightSlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(text, new(Plot.Left + (Plot.Width - text.Width) / 2, Plot.Bottom + 4));
        var end = new FormattedText("100%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, Brushes.LightSlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(end, new(Plot.Right - end.Width, Plot.Bottom + 4));
    }

    private void DrawCurve(DrawingContext context, IReadOnlyList<PlotSample>? samples, Pen pen)
    {
        if (samples is null) { return; }
        for (var i = 1; i < samples.Count; i++)
        {
            context.DrawLine(pen, Screen(new(samples[i - 1].Time, samples[i - 1].Output)), Screen(new(samples[i].Time, samples[i].Output)));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Points is not { Count: >= 2 } points) { return; }
        Focus();
        var position = e.GetPosition(this);
        selected = Hit(position);
        if (e.ClickCount == 2 && selected < 0)
        {
            var p = Fraction(position);
            Commit(CurveEditing.Insert(points, p.Input, p.Output));
            selected = Hit(position);
        }
        if (selected > 0 && selected < Points!.Count - 1) { CaptureMouse(); }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed && Points is { } points)
        {
            var p = Fraction(e.GetPosition(this));
            Commit(CurveEditing.Move(points, selected, p.Input, p.Output));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); ReleaseMouseCapture(); }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (Points is { } points) { Commit(CurveEditing.Remove(points, Hit(e.GetPosition(this)))); selected = -1; }
        e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Points is not { } points) { return; }
        if (e.Key == Key.Tab) { return; }
        if (e.Key is Key.Home) { selected = points.Count > 2 ? 1 : -1; }
        else if (selected > 0 && selected < points.Count - 1)
        {
            var p = points[selected];
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .05 : .005;
            if (e.Key is Key.Delete or Key.Back) { Commit(CurveEditing.Remove(points, selected)); selected = -1; }
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                Commit(CurveEditing.Move(points, selected, p.Input + (e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0),
                    p.Output + (e.Key == Key.Down ? -step : e.Key == Key.Up ? step : 0)));
            }
        }
        InvalidateVisual();
        e.Handled = true;
    }
    private int Hit(Point position)
    {
        if (Points is { } points)
        {
            for (var i = 0; i < points.Count; i++) { if ((Screen(points[i]) - position).Length <= 10) { return i; } }
        }
        return -1;
    }
    private void Commit(CurvePoint[] points)
    {
        if (Points is { } previous && previous.SequenceEqual(points)) { return; }
        SetCurrentValue(PointsProperty, points);
        Edited?.Invoke(this, EventArgs.Empty);
    }
    private static void OnPointsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CurveEditor)sender;
        if (editor.selected >= editor.Points?.Count) { editor.selected = -1; }
    }
}
