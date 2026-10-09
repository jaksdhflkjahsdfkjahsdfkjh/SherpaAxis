namespace SherpaAxis.App.Models;

public sealed class GraphViewport
{
    private readonly double minimum;
    private readonly double maximum;
    public GraphViewport(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum) { throw new ArgumentOutOfRangeException(nameof(maximum)); }
        this.minimum = Start = minimum;
        this.maximum = End = maximum;
    }
    public double Start { get; private set; }
    public double End { get; private set; }
    public double Duration => End - Start;

    public void Zoom(double fraction, int wheelDelta)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var span = Math.Clamp(Duration * Math.Pow(.75, wheelDelta / 120d), Math.Min(.1, maximum - minimum), maximum - minimum);
        var anchor = Start + Math.Clamp(fraction, 0, 1) * Duration;
        SetRange(anchor - fraction * span, span);
    }

    public void Select(double first, double last)
    {
        var left = Start + Math.Clamp(Math.Min(first, last), 0, 1) * Duration;
        var span = Math.Abs(Math.Clamp(last, 0, 1) - Math.Clamp(first, 0, 1)) * Duration;
        if (span >= Math.Min(.1, maximum - minimum)) { SetRange(left, span); }
    }

    private void SetRange(double start, double span)
    {
        Start = Math.Clamp(start, minimum, maximum - span);
        End = Start + span;
    }
}
