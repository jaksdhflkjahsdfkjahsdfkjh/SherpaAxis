using System.Windows;

namespace SherpaAxis.App.Services.Desktop;

internal static class OverlayPlacement
{
    internal static Point Constrain(Point desired, Size size, IReadOnlyList<Rect> workAreas)
    {
        if (workAreas.Count == 0) { return desired; }
        var bounds = new Rect(desired, size);
        // Prefer the screen containing most of the panel, then the nearest screen if it was unplugged.
        var area = workAreas.OrderByDescending(screen =>
        {
            var intersection = Rect.Intersect(bounds, screen);
            return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
        }).ThenBy(screen =>
        {
            var x = desired.X - Math.Clamp(desired.X, screen.Left, screen.Right);
            var y = desired.Y - Math.Clamp(desired.Y, screen.Top, screen.Bottom);
            return x * x + y * y;
        }).First();
        return new(Math.Clamp(desired.X, area.Left, Math.Max(area.Left, area.Right - size.Width)),
            Math.Clamp(desired.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)));
    }
}
