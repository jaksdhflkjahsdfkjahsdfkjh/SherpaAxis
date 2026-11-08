using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Themes;

namespace SherpaAxis.App.Controls;

internal sealed class CombinedPedalPlot : FrameworkElement
{
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(nameof(Frame), typeof(TelemetryFrame), typeof(CombinedPedalPlot), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(nameof(WindowSeconds), typeof(double), typeof(CombinedPedalPlot), new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsRender), value => value is double number && double.IsFinite(number) && number is >= 1 and <= 60);
    public static readonly DependencyProperty ShowRawProperty = DependencyProperty.Register(nameof(ShowRaw), typeof(bool), typeof(CombinedPedalPlot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowFilteredProperty = DependencyProperty.Register(nameof(ShowFiltered), typeof(bool), typeof(CombinedPedalPlot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowOutputProperty = DependencyProperty.Register(nameof(ShowOutput), typeof(bool), typeof(CombinedPedalPlot), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public TelemetryFrame? Frame { get => (TelemetryFrame?)GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    public bool ShowRaw { get => (bool)GetValue(ShowRawProperty); set => SetValue(ShowRawProperty, value); }
    public bool ShowFiltered { get => (bool)GetValue(ShowFilteredProperty); set => SetValue(ShowFilteredProperty, value); }
    public bool ShowOutput { get => (bool)GetValue(ShowOutputProperty); set => SetValue(ShowOutputProperty, value); }

    public CombinedPedalPlot() => PropertyChangedEventManager.AddHandler(Localizer.Instance, (_, _) => InvalidateVisual(), nameof(Localizer.Language));

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 120 || ActualHeight < 60) { return; }
        var area = new Rect(2, 5, ActualWidth - 4, ActualHeight - 10);
        for (var tick = 0; tick <= 4; tick++)
        {
            var y = area.Bottom - area.Height * tick / 4;
            drawing.DrawLine(new Pen(TelemetryColors.Grid, 1), new Point(area.Left, y), new Point(area.Right, y));
        }
        if (Frame is not { } frame || frame.Series.All(series => series.Samples.Count == 0))
        {
            return;
        }
        if (!ShowRaw && !ShowFiltered && !ShowOutput)
        {
            Label(drawing, Localizer.Translate("Choose a signal in Settings → Pedal overlay."), area.Left + 16, area.Top + 16, AppColors.MutedBrush);
            return;
        }
        drawing.PushClip(new RectangleGeometry(area));
        foreach (var series in frame.Series)
        {
            var brush = TelemetryColors.ForPedal(series.Axis);
            if (ShowRaw) { Draw(series, SignalGraph.Input, new Pen(brush, 1.2) { DashStyle = DashStyles.Dash }); }
            if (ShowFiltered) { Draw(series, point => point.Filtered, new Pen(brush, 1.7) { DashStyle = DashStyles.Dot }); }
            if (ShowOutput) { Draw(series, point => point.Output, new Pen(brush, series.Axis == SherpaAxis.Profiles.PedalAxis.Clutch ? 1.5 : 2.6) { LineJoin = PenLineJoin.Round }); }
        }
        drawing.Pop();

        void Draw(PedalHistory series, Func<PlotSample, double> value, Pen pen)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var first = true;
                foreach (var point in series.Samples)
                {
                    if (point.Time < frame.End - WindowSeconds || point.Time > frame.End) { continue; }
                    var number = value(point);
                    if (!double.IsFinite(number)) { first = true; continue; }
                    var location = new Point(area.Left + (point.Time - frame.End + WindowSeconds) / WindowSeconds * area.Width, area.Bottom - Math.Clamp(number, 0, 1) * area.Height);
                    if (first) { context.BeginFigure(location, false, false); first = false; }
                    else { context.LineTo(location, true, false); }
                }
            }
            geometry.Freeze(); drawing.DrawGeometry(null, pen, geometry);
        }
    }

    private void Label(DrawingContext drawing, string text, double x, double y, Brush brush)
    {
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, ActualWidth - x - 5), MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis };
        drawing.DrawText(label, new Point(x, y));
    }
}
