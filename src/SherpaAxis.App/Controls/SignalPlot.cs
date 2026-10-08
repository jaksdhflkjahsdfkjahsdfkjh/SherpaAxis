using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SherpaAxis.App.Models;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Controls;

public sealed class SignalPlot : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series),
        typeof(IReadOnlyList<PedalHistory>), typeof(SignalPlot), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PedalProperty = DependencyProperty.Register(nameof(Pedal),
        typeof(string), typeof(SignalPlot), new FrameworkPropertyMetadata("All pedals", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode),
        typeof(string), typeof(SignalPlot), new FrameworkPropertyMetadata("Input vs filtered", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom),
        typeof(bool), typeof(SignalPlot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(nameof(WindowSeconds),
        typeof(double), typeof(SignalPlot), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double d && double.IsFinite(d) && d > 0);

    public IReadOnlyList<PedalHistory>? Series { get => (IReadOnlyList<PedalHistory>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public string Pedal { get => (string)GetValue(PedalProperty); set => SetValue(PedalProperty, value); }
    public string Mode { get => (string)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public bool Zoom { get => (bool)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth < 120 || ActualHeight < 60) { return; }
        var frame = SignalGraph.Prepare(Series ?? [], Pedal, Mode, Zoom, WindowSeconds);
        if (frame.Lanes.Count == 0 || frame.Lanes.All(lane => lane.Samples.Count == 0))
        {
            Label(drawingContext, "Start live input or open a recording to see all three pedals", 16, 30, Brushes.LightSlateGray);
            return;
        }
        var laneHeight = (ActualHeight - 20) / frame.Lanes.Count;
        for (var index = 0; index < frame.Lanes.Count; index++)
        {
            var lane = frame.Lanes[index];
            var top = index * laneHeight;
            var color = new SolidColorBrush(lane.Axis switch
            {
                PedalAxis.Throttle => Color.FromRgb(69, 214, 187),
                PedalAxis.Brake => Color.FromRgb(255, 185, 103),
                _ => Color.FromRgb(175, 148, 246)
            });
            Label(drawingContext, $"{lane.Axis}   ·   {lane.FilterLabel}", 0, top, color, ActualWidth - 12);
            var left = Mode == "Device counts" ? 82 : 52;
            var area = new Rect(left, top + 22, Math.Max(1, ActualWidth - left - 8), Math.Max(1, laneHeight - 34));
            var grid = new Pen(new SolidColorBrush(Color.FromRgb(39, 54, 75)), 1);
            for (var tick = 0; tick <= 2; tick++)
            {
                var y = area.Top + tick * area.Height / 2;
                drawingContext.DrawLine(grid, new(area.Left, y), new(area.Right, y));
                var value = lane.Maximum - tick * (lane.Maximum - lane.Minimum) / 2;
                var label = Mode == "Device counts" ? value.ToString("N0", CultureInfo.InvariantCulture)
                    : (value * 100).ToString(Mode == "Filter difference" || Zoom ? "0.##" : "0", CultureInfo.InvariantCulture)
                        + (Mode == "Filter difference" ? " pp" : "%");
                Label(drawingContext, label, 0, y - 6, Brushes.LightSlateGray);
            }
            drawingContext.PushClip(new RectangleGeometry(area));
            if (Mode == "Input vs filtered")
            {
                DrawSeries(SignalGraph.Input, new Pen(Brushes.LightSlateGray, 1.3) { DashStyle = DashStyles.Dash });
            }
            DrawSeries(sample => SignalGraph.Value(sample, Mode), new Pen(color, 1.8));
            drawingContext.Pop();

            void DrawSeries(Func<PlotSample, double> value, Pen pen)
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    var first = true;
                    foreach (var sample in lane.Samples)
                    {
                        var point = new Point(area.Left + (sample.Time - frame.Start) / Math.Max(.001, frame.End - frame.Start) * area.Width,
                            area.Bottom - (value(sample) - lane.Minimum) / (lane.Maximum - lane.Minimum) * area.Height);
                        if (first) { context.BeginFigure(point, false, false); first = false; }
                        else { context.LineTo(point, true, false); }
                    }
                }
                geometry.Freeze();
                drawingContext.DrawGeometry(null, pen, geometry);
            }
        }
        Label(drawingContext, FormattableString.Invariant($"{frame.Start:F1} s"), 52, ActualHeight - 16, Brushes.LightSlateGray);
        Label(drawingContext, FormattableString.Invariant($"{frame.End:F1} s"), ActualWidth - 65, ActualHeight - 16, Brushes.LightSlateGray);
    }

    private void Label(DrawingContext context, string text, double x, double y, Brush brush, double maximumWidth = double.PositiveInfinity)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (double.IsFinite(maximumWidth)) { formatted.MaxTextWidth = Math.Max(1, maximumWidth); formatted.MaxLineCount = 1; formatted.Trimming = TextTrimming.CharacterEllipsis; }
        context.DrawText(formatted, new(x, y));
    }
}
