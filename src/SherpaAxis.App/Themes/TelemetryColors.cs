using System.Windows.Media;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Themes;

internal static class TelemetryColors
{
    internal static SolidColorBrush Throttle { get; } = Brush(Color.FromRgb(42, 225, 39));
    internal static SolidColorBrush Brake { get; } = Brush(Color.FromRgb(255, 55, 55));
    internal static SolidColorBrush Clutch { get; } = Brush(Color.FromRgb(64, 156, 255));
    internal static SolidColorBrush Grid { get; } = Brush(Color.FromArgb(25, 230, 230, 235));
    internal static SolidColorBrush ForPedal(PedalAxis axis) => axis switch { PedalAxis.Throttle => Throttle, PedalAxis.Brake => Brake, _ => Clutch };
    private static SolidColorBrush Brush(Color color) { var value = new SolidColorBrush(color); value.Freeze(); return value; }
}
