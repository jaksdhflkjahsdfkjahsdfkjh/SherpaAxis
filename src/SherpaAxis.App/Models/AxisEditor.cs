using System.Globalization;
using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed record AxisEditor
{
    public string Minimum { get; set; } = "0";
    public string Maximum { get; set; } = "1";
    public bool Inverted { get; set; }
    public string LowerDeadzone { get; set; } = "0";
    public string UpperDeadzone { get; set; } = "0";
    public string CurveKind { get; set; } = "Linear";
    public string Exponent { get; set; } = "1";
    public string Points { get; set; } = "0:0; 0.5:0.5; 1:1";
    public string FilterMilliseconds { get; set; } = "0";
    public string FilterGapMilliseconds { get; set; } = "100";
    public string MaximumCorrection { get; set; } = "0";
    public string CorrectionRate { get; set; } = "1000";
    public string CorrectionOutputRate { get; set; } = "0.05";
    public string SessionGapMilliseconds { get; set; } = "100";

    public AxisProfile BuildProfile()
    {
        var filter = Number(FilterMilliseconds, "Filter time constant");
        if (filter < 0)
        {
            throw new ArgumentException("Filter time constant must be nonnegative; zero disables it.");
        }

        var gap = Number(FilterGapMilliseconds, "Filter gap") / 1000;
        if (gap <= 0)
        {
            throw new ArgumentException("Filter gap must be positive.");
        }

        var curve = CurveKind switch
        {
            "Linear" => ProfileCurve.Linear,
            "Power" => ProfileCurve.Power(Number(Exponent, "Exponent")),
            "Custom" => ProfileCurve.PiecewiseLinear(ParsePoints()),
            _ => throw new ArgumentException("Select a supported curve.")
        };
        return new(new(Number(Minimum, "Minimum"), Number(Maximum, "Maximum"), Inverted),
            new(Number(LowerDeadzone, "Lower deadzone") / 100, Number(UpperDeadzone, "Upper deadzone") / 100),
            curve, Number(MaximumCorrection, "Maximum correction"), filter == 0 ? null : new(filter / 1000, gap));
    }

    public AxisSessionSettings BuildSessionSettings() => new(Number(CorrectionRate, "Correction rate"),
        Number(CorrectionOutputRate, "Correction output rate"), Number(SessionGapMilliseconds, "Session gap") / 1000);

    public static AxisEditor FromProfile(AxisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new()
        {
            Minimum = Format(profile.Calibration.Minimum),
            Maximum = Format(profile.Calibration.Maximum),
            Inverted = profile.Calibration.Inverted,
            LowerDeadzone = Format(profile.Deadzone.Lower * 100),
            UpperDeadzone = Format(profile.Deadzone.Upper * 100),
            CurveKind = profile.Curve.Kind switch { "power" => "Power", "piecewiseLinear" => "Custom", _ => "Linear" },
            Exponent = Format(profile.Curve.Exponent ?? 1),
            Points = profile.Curve.Kind == "piecewiseLinear"
                ? string.Join("; ", profile.Curve.Points.Select(point => $"{Format(point.Input)}:{Format(point.Output)}"))
                : "0:0; 0.5:0.5; 1:1",
            FilterMilliseconds = Format((profile.Filter?.TimeConstantSeconds ?? 0) * 1000),
            FilterGapMilliseconds = Format((profile.Filter?.ResetAfterSeconds ?? .1) * 1000),
            MaximumCorrection = Format(profile.MaximumBaselineCorrection)
        };
    }

    private CurvePoint[] ParsePoints()
    {
        var fields = Points.Split(';', StringSplitOptions.TrimEntries);
        if (fields.Length > ProfileCurve.MaximumControlPoints)
        {
            throw new ArgumentException("Too many curve points.");
        }

        return fields.Select(field =>
        {
            var pair = field.Split(':', StringSplitOptions.TrimEntries);
            return pair.Length != 2 ? throw new ArgumentException("Curve points use input:output; input:output, within 0..1.")
                : new CurvePoint(Number(pair[0], "Curve input"), Number(pair[1], "Curve output"));
        }).ToArray();
    }

    private static double Number(string value, string field) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result)
            ? result : throw new ArgumentException($"{field} requires a finite number using a decimal point.");

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
