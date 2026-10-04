using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SherpaAxis.App.Models;

namespace SherpaAxis.App.Controls;

public sealed class SignalPlot : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(IReadOnlyList<PlotSample>), typeof(SignalPlot), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsCurveProperty = DependencyProperty.Register(nameof(IsCurve),
        typeof(bool), typeof(SignalPlot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CountsModeProperty = DependencyProperty.Register(nameof(CountsMode),
        typeof(bool), typeof(SignalPlot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<PlotSample>? Samples
    {
        get => (IReadOnlyList<PlotSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public bool IsCurve
    {
        get => (bool)GetValue(IsCurveProperty);
        set => SetValue(IsCurveProperty, value);
    }

    public bool CountsMode
    {
        get => (bool)GetValue(CountsModeProperty);
        set => SetValue(CountsModeProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var left = CountsMode ? 88 : 36;
        var width = ActualWidth - left - 12;
        var height = ActualHeight - 32;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var samples = Samples;
        var end = samples is { Count: > 0 } ? samples[^1].Time : 0;
        var start = samples is { Count: > 0 } && !IsCurve ? Math.Max(samples[0].Time, end - 12) : 0;
        var span = Math.Max(.001, end - start);
        var minimum = 0d;
        var maximum = 1d;
        if (CountsMode && samples is { Count: > 0 })
        {
            var visible = samples.Where(sample => sample.Time >= start).Select(sample => sample.DeviceInput).ToArray();
            minimum = visible.Min();
            maximum = visible.Max();
            var padding = Math.Max(2, (maximum - minimum) * .1);
            minimum -= padding;
            maximum += padding;
        }

        var grid = new Pen(new SolidColorBrush(Color.FromRgb(39, 54, 75)), 1);
        for (var index = 0; index <= 4; index++)
        {
            var y = 6 + height * index / 4;
            drawingContext.DrawLine(grid, new(left, y), new(left + width, y));
            var label = CountsMode ? (maximum - (maximum - minimum) * index / 4).ToString("N0", CultureInfo.InvariantCulture)
                : (100 - 25 * index).ToString(CultureInfo.InvariantCulture);
            Label(drawingContext, label, 0, y - 7);
        }

        if (samples is null || samples.Count == 0)
        {
            Label(drawingContext, "Start live input or open a capture", 52, 20 + height / 2);
            return;
        }

        if (CountsMode)
        {
            DrawSeries(sample => (sample.DeviceInput - minimum) / (maximum - minimum), Color.FromRgb(69, 214, 187), 1.5);
        }
        else
        {
            DrawSeries(sample => sample.Input, Color.FromRgb(145, 165, 190), 1.2);
            if (!IsCurve)
            {
                DrawSeries(sample => sample.Filtered, Color.FromRgb(175, 148, 246), 1.5);
            }

            DrawSeries(sample => sample.Output, Color.FromRgb(69, 214, 187), 2);
        }

        Label(drawingContext, IsCurve ? "0% input" : FormattableString.Invariant($"{start:F1} s"), left, height + 14);
        Label(drawingContext, IsCurve ? "100%" : FormattableString.Invariant($"{end:F1} s"), left + width - 44, height + 14);

        void DrawSeries(Func<PlotSample, double> select, Color color, double thickness)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var first = true;
                foreach (var sample in samples)
                {
                    if (sample.Time < start)
                    {
                        continue;
                    }

                    var point = new Point(left + (sample.Time - start) / span * width,
                        6 + (1 - Math.Clamp(select(sample), 0, 1)) * height);
                    if (first)
                    {
                        context.BeginFigure(point, false, false);
                        first = false;
                    }
                    else
                    {
                        context.LineTo(point, true, false);
                    }
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, new Pen(new SolidColorBrush(color), thickness), geometry);
        }
    }

    private void Label(DrawingContext context, string text, double x, double y) => context.DrawText(
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
            new SolidColorBrush(Color.FromRgb(146, 165, 188)), VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
}
