using System.Windows.Media;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Themes;

public static class AppColors
{
    public static Color Window { get; } = Color.FromRgb(11, 11, 19);
    public static Color Workspace { get; } = Color.FromRgb(18, 18, 27);
    public static Color Panel { get; } = Color.FromRgb(30, 30, 39);
    public static Color Header { get; } = Color.FromRgb(36, 36, 46);
    public static Color Surface { get; } = Color.FromRgb(39, 39, 49);
    public static Color Input { get; } = Color.FromRgb(24, 24, 33);
    public static Color Canvas { get; } = Color.FromRgb(17, 17, 26);
    public static Color Hover { get; } = Color.FromRgb(48, 48, 60);
    public static Color Border { get; } = Color.FromRgb(45, 45, 57);
    public static Color Grid { get; } = Color.FromRgb(39, 39, 51);
    public static Color ControlBorder { get; } = Color.FromRgb(100, 100, 120);
    public static Color Text { get; } = Color.FromRgb(242, 242, 247);
    public static Color Muted { get; } = Color.FromRgb(176, 176, 191);
    public static Color Accent { get; } = Color.FromRgb(1, 89, 207);
    public static Color AccentForeground { get; } = Color.FromRgb(128, 182, 255);
    public static Color Brake { get; } = Color.FromRgb(241, 180, 104);
    public static Color AccentHover { get; } = Color.FromRgb(22, 108, 224);
    public static Color AccentText { get; } = Color.FromRgb(248, 249, 252);
    public static Color AccentSoft { get; } = Color.FromArgb(56, 1, 89, 207);
    public static Color Selection { get; } = Color.FromRgb(12, 52, 109);
    public static Color Success { get; } = Color.FromRgb(142, 204, 156);
    public static Color Caution { get; } = Color.FromRgb(239, 190, 118);
    public static Color Problem { get; } = Color.FromRgb(240, 155, 159);
    public static Color Clutch { get; } = Color.FromRgb(141, 179, 230);
    public static Color Overlay { get; } = Color.FromArgb(175, 8, 8, 15);

    public static SolidColorBrush MutedBrush { get; } = CreateBrush(Muted);
    public static SolidColorBrush TextBrush { get; } = CreateBrush(Text);
    public static SolidColorBrush BorderBrush { get; } = CreateBrush(Border);
    public static SolidColorBrush GridBrush { get; } = CreateBrush(Grid);
    public static SolidColorBrush AccentBrush { get; } = CreateBrush(Accent);
    public static SolidColorBrush AccentForegroundBrush { get; } = CreateBrush(AccentForeground);
    public static SolidColorBrush BrakeBrush { get; } = CreateBrush(Brake);
    public static SolidColorBrush AccentSoftBrush { get; } = CreateBrush(AccentSoft);
    public static SolidColorBrush OverlayBrush { get; } = CreateBrush(Overlay);
    public static SolidColorBrush ThrottleBrush { get; } = CreateBrush(Success);
    public static SolidColorBrush ClutchBrush { get; } = CreateBrush(Clutch);

    public static SolidColorBrush ForPedal(PedalAxis axis) => axis switch
    {
        PedalAxis.Clutch => ClutchBrush,
        PedalAxis.Throttle => ThrottleBrush,
        _ => BrakeBrush
    };

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
