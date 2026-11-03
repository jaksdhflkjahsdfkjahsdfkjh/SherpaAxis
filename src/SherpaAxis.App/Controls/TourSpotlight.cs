using System.Windows;
using System.Windows.Media;
using SherpaAxis.App.Themes;

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
        { drawingContext.DrawGeometry(AppColors.OverlayBrush, null, outer); return; }
        var hole = new RectangleGeometry(TargetBounds, 8, 8);
        drawingContext.DrawGeometry(AppColors.OverlayBrush, null,
            new CombinedGeometry(GeometryCombineMode.Exclude, outer, hole));
        drawingContext.DrawRoundedRectangle(null, new Pen(AppColors.AccentBrush, 2), TargetBounds, 8, 8);
    }
}
