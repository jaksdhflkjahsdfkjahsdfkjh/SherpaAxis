using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.Cli;

internal static class AxisProfileOptions
{
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly<string>(
        ["--minimum", "--maximum", "--invert", "--max-correction", "--deadzone-low", "--deadzone-high",
            "--exponent", "--filter-ms", "--filter-reset-ms"]);

    public static AxisProfile Parse(OptionValues options)
    {
        var milliseconds = options.Number("--filter-ms", 0);
        if (milliseconds < 0 || (milliseconds == 0 && options.Contains("--filter-reset-ms")))
        {
            throw new ArgumentException("--filter-ms must be nonnegative; --filter-reset-ms requires an enabled filter.");
        }

        var filter = milliseconds == 0 ? null : new LowPassFilterSettings(milliseconds / 1000,
            options.Number("--filter-reset-ms", 100) / 1000);
        return new(new AxisCalibration(options.Number("--minimum"), options.Number("--maximum"), options.Boolean("--invert")),
            new AxisDeadzone(options.Number("--deadzone-low", 0), options.Number("--deadzone-high", 0)),
            options.Contains("--exponent") ? ProfileCurve.Power(options.Number("--exponent")) : ProfileCurve.Linear,
            options.Number("--max-correction", 0), filter);
    }
}
