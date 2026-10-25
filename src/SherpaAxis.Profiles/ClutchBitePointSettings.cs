namespace SherpaAxis.Profiles;

public enum ClutchBitePointMode { HoldMinimum, HoldFullRange }

public sealed record ClutchBitePointSettings
{
    public ClutchBitePointSettings(double output = .5, CorrectionButton? button = null, ClutchBitePointMode mode = ClutchBitePointMode.HoldMinimum)
    {
        if (!double.IsFinite(output) || output is < 0 or > 1) { throw new ArgumentOutOfRangeException(nameof(output), "Bite point output must be between 0% and 100%."); }
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        Output = output;
        Button = button;
        Mode = mode;
    }
    public double Output { get; }
    public CorrectionButton? Button { get; }
    public ClutchBitePointMode Mode { get; }
}
