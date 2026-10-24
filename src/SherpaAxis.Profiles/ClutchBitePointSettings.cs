namespace SherpaAxis.Profiles;

public sealed record ClutchBitePointSettings
{
    public ClutchBitePointSettings(double output = .5, CorrectionButton? button = null)
    {
        if (!double.IsFinite(output) || output is < 0 or > 1) { throw new ArgumentOutOfRangeException(nameof(output), "Bite point output must be between 0% and 100%."); }
        Output = output;
        Button = button;
    }
    public double Output { get; }
    public CorrectionButton? Button { get; }
}
