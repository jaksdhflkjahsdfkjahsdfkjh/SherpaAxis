using SherpaAxis.App.Models;

namespace SherpaAxis.App.ViewModels;

public sealed class PedalOverlaySettingsViewModel : ObservableObject
{
    private double windowSeconds = 10, width = 640, height = 140;
    private bool showRaw, showFiltered, showOutput = true, graphPaused, visible, locked;
    private double? left, top;
    internal bool Loading { get; private set; }
    public double WindowSeconds { get => windowSeconds; set => Set(ref windowSeconds, Valid(value, 1, 60)); }
    public double Width { get => width; set => Set(ref width, Valid(value, 360, 1000)); }
    public double Height { get => height; set => Set(ref height, Valid(value, 110, 220)); }
    public bool ShowRaw { get => showRaw; set => Set(ref showRaw, value); }
    public bool ShowFiltered { get => showFiltered; set => Set(ref showFiltered, value); }
    public bool ShowOutput { get => showOutput; set => Set(ref showOutput, value); }
    public bool GraphPaused { get => graphPaused; set => Set(ref graphPaused, value); }
    public bool IsLocked { get => locked; set { if (Set(ref locked, value)) { Changed(nameof(CanEditPlacement)); } } }
    public bool CanEditPlacement => !IsLocked;
    public bool IsVisible
    {
        get => visible;
        internal set { if (Set(ref visible, value)) { Changed(nameof(CanShow)); Changed(nameof(CanHide)); } }
    }
    public bool CanShow => !IsVisible;
    public bool CanHide => IsVisible;
    // Screen-pixel coordinates allow restoring the same desktop location across DPI scales.
    public double? Left => left;
    public double? Top => top;
    internal void SetPosition(double x, double y)
    {
        if (!ValidCoordinate(x) || !ValidCoordinate(y)) { throw new ArgumentOutOfRangeException(nameof(x)); }
        if (left == x && top == y) { return; }
        left = x; top = y;
        Changed(nameof(Left)); Changed(nameof(Top));
    }
    internal PedalOverlayPreferences? Snapshot()
    {
        var value = new PedalOverlayPreferences(WindowSeconds, ShowRaw, ShowFiltered, ShowOutput, Width, Height, Left, Top, IsLocked);
        return value == new PedalOverlayPreferences() ? null : value;
    }
    internal void Load(PedalOverlayPreferences? value)
    {
        Loading = true;
        try
        {
            value ??= new();
            WindowSeconds = InRange(value.WindowSeconds, 1, 60) ? value.WindowSeconds : 10;
            Width = InRange(value.Width, 360, 1000) ? value.Width : 640;
            Height = InRange(value.Height, 110, 220) ? value.Height : 140;
            ShowRaw = value.ShowRaw; ShowFiltered = value.ShowFiltered; ShowOutput = value.ShowOutput;
            IsLocked = value.Locked;
            var validPosition = value.Left is { } x && ValidCoordinate(x) && value.Top is { } y && ValidCoordinate(y);
            left = validPosition ? value.Left : null; top = validPosition ? value.Top : null;
            Changed(nameof(Left)); Changed(nameof(Top));
        }
        finally { Loading = false; Changed(nameof(Loading)); }
    }
    private static bool ValidCoordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 1000000;
    private static bool InRange(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
    private static double Valid(double value, double minimum, double maximum) => InRange(value, minimum, maximum) ? value : throw new ArgumentOutOfRangeException(nameof(value));
}
