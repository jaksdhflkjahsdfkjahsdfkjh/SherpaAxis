using System.Windows;
using System.Windows.Media;

namespace SherpaAxis.App.Controls;

public sealed class TourSpotlight : FrameworkElement
{
    public Rect TargetBounds { get; private set; } = Rect.Empty;

    public void SetTarget(Rect target)
    {
        if (target == TargetBounds) { return; }
        TargetBounds = target;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var outer = new RectangleGeometry(new Rect(RenderSize));
        if (TargetBounds.IsEmpty)
        { drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(175, 3, 8, 16)), null, outer); return; }
        var hole = new RectangleGeometry(TargetBounds, 8, 8);
        drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(175, 3, 8, 16)), null,
            new CombinedGeometry(GeometryCombineMode.Exclude, outer, hole));
        drawingContext.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(69, 214, 187)), 2), TargetBounds, 8, 8);
    }
}
