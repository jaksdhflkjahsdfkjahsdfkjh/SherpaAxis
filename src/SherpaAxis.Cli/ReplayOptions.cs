using System.Globalization;
using SherpaAxis.Core;

namespace SherpaAxis.Cli;

internal sealed record ReplayOptions(string InputPath, string OutputPath, string Axis,
    AxisCalibration Calibration, AxisDeadzone Deadzone, ResponseCurve Curve,
    double? Baseline, double MaximumCorrection, double From, double To, LowPassFilterSettings? FilterSettings)
{
    public AxisProcessor CreateProcessor() => new(Calibration, Deadzone, Curve, MaximumCorrection, FilterSettings);

    public static ReplayOptions Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] allowed = ["--input", "--csv", "--axis", "--minimum", "--maximum", "--invert",
            "--baseline", "--max-correction", "--deadzone-low", "--deadzone-high", "--exponent", "--from", "--to",
            "--filter-ms", "--filter-reset-ms"];
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var option = arguments[index];
            if (!allowed.Contains(option, StringComparer.Ordinal) || index + 1 >= arguments.Length ||
                string.IsNullOrWhiteSpace(arguments[index + 1]) || !values.TryAdd(option, arguments[index + 1]))
            {
                throw new ArgumentException($"Unknown, missing, or repeated replay option: {option}");
            }
        }

        string Required(string key) => values.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Replay requires {key}.");
        double Number(string key, double? fallback = null)
        {
            if (!values.TryGetValue(key, out var value) && fallback.HasValue)
            {
                return fallback.Value;
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            {
                throw new ArgumentException($"{key} requires a finite number using a decimal point.");
            }

            return number;
        }

        var axis = values.GetValueOrDefault("--axis", "brake");
        if (axis is not ("brake" or "throttle" or "clutch"))
        {
            throw new ArgumentException("--axis must be brake, throttle, or clutch.");
        }

        var invert = false;
        if (values.TryGetValue("--invert", out var inverted) && !bool.TryParse(inverted, out invert))
        {
            throw new ArgumentException("--invert requires true or false.");
        }

        if (values.ContainsKey("--baseline") != values.ContainsKey("--max-correction"))
        {
            throw new ArgumentException("--baseline and --max-correction must be provided together.");
        }

        var calibration = new AxisCalibration(Number("--minimum"), Number("--maximum"), invert);
        var deadzone = new AxisDeadzone(Number("--deadzone-low", 0), Number("--deadzone-high", 0));
        var curve = ResponseCurve.Power(Number("--exponent", 1));
        double? baseline = values.ContainsKey("--baseline") ? Number("--baseline") : null;
        var maximumCorrection = Number("--max-correction", 0);
        var filterMilliseconds = Number("--filter-ms", 0);
        if (filterMilliseconds < 0 || (filterMilliseconds == 0 && values.ContainsKey("--filter-reset-ms")))
        {
            throw new ArgumentException("--filter-ms must be nonnegative; --filter-reset-ms requires an enabled filter.");
        }

        var filterSettings = filterMilliseconds == 0 ? null : new LowPassFilterSettings(
            filterMilliseconds / 1000, Number("--filter-reset-ms", 100) / 1000);
        var processor = new AxisProcessor(calibration, deadzone, curve, maximumCorrection, filterSettings);
        if (baseline.HasValue)
        {
            processor.ConfirmReleasedBaseline(baseline.Value);
        }

        var from = Number("--from", 0);
        var to = Number("--to", double.MaxValue);
        if (from < 0 || to < from)
        {
            throw new ArgumentException("Replay requires 0 <= --from <= --to.");
        }

        return new(Required("--input"), Required("--csv"), axis, calibration, deadzone, curve,
            baseline, maximumCorrection, from, to, filterSettings);
    }
}
