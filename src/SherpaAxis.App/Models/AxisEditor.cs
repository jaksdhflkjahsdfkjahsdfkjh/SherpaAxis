using System.Globalization;
using System.Text.Json.Serialization;
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
    public string FilterKind { get; set; } = "Exponential";
    public string FilterWindowSamples { get; set; } = "5";
    [JsonIgnore]
    public bool FilterEnabled
    {
        get => double.TryParse(FilterMilliseconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0;
        set { if (!value) { FilterMilliseconds = "0"; } else if (!FilterEnabled) { FilterMilliseconds = "10"; } }
    }
    public string MaximumCorrection { get; set; } = "0";
    public string CorrectionRate { get; set; } = "1000";
    public string CorrectionOutputRate { get; set; } = "0.05";
    public string SessionGapMilliseconds { get; set; } = "100";
    public BaselineCorrectionMode CorrectionMode { get; set; }
    public CorrectionButton? CorrectionButton { get; set; }
    public string BitePointOutput { get; set; } = "50";
    public CorrectionButton? BitePointButton { get; set; }

    [JsonIgnore]
    internal bool HasRecoverableText => new[]
    {
        Minimum, Maximum, LowerDeadzone, UpperDeadzone, CurveKind, Exponent, Points,
        FilterMilliseconds, FilterGapMilliseconds, FilterKind, FilterWindowSamples, MaximumCorrection,
        CorrectionRate, CorrectionOutputRate, SessionGapMilliseconds, BitePointOutput
    }.All(value => value is not null && value.Length <= 128 * 1024);

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

        var curve = BuildCurve();
        if (!Enum.TryParse<SignalFilterKind>(FilterKind, out var kind) || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("Select a supported filter.");
        }
        if (!int.TryParse(FilterWindowSamples, NumberStyles.None, CultureInfo.InvariantCulture, out var window))
        {
            throw new ArgumentException("Filter window requires an odd integer from 3 to 101.");
        }
        return new(new(Number(Minimum, "Minimum"), Number(Maximum, "Maximum"), Inverted),
            new(Number(LowerDeadzone, "Lower deadzone") / 100, Number(UpperDeadzone, "Upper deadzone") / 100),
            curve, Number(MaximumCorrection, "Maximum correction"), filter == 0 ? null : new(filter / 1000, gap, kind, window), BuildSessionSettings(), CorrectionMode, CorrectionButton, BuildBitePoint());
    }

    private ClutchBitePointSettings? BuildBitePoint()
    {
        var output = Number(BitePointOutput, "Bite point output") / 100;
        return BitePointButton is not null || output != .5 ? new(output, BitePointButton) : null;
    }

    public ProfileCurve BuildCurve() => CurveKind switch
    {
        "Linear" => ProfileCurve.Linear,
        "Power" => ProfileCurve.Power(Number(Exponent, "Exponent")),
        "Custom" => ProfileCurve.PiecewiseLinear(ParsePoints()),
        _ => CurvePresets.Create(CurveKind)
    };

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
            CurveKind = CurvePresets.Identify(profile.Curve),
            Exponent = Format(profile.Curve.Exponent ?? 1),
            Points = profile.Curve.Kind == "piecewiseLinear"
                ? string.Join("; ", profile.Curve.Points.Select(point => $"{Format(point.Input)}:{Format(point.Output)}"))
                : "0:0; 0.5:0.5; 1:1",
            FilterMilliseconds = Format((profile.Filter?.TimeConstantSeconds ?? 0) * 1000),
            FilterGapMilliseconds = Format((profile.Filter?.ResetAfterSeconds ?? .1) * 1000),
            FilterKind = (profile.Filter?.Kind ?? SignalFilterKind.Exponential).ToString(),
            FilterWindowSamples = (profile.Filter?.WindowSamples ?? 5).ToString(CultureInfo.InvariantCulture),
            MaximumCorrection = Format(profile.MaximumBaselineCorrection),
            CorrectionMode = profile.CorrectionMode,
            CorrectionButton = profile.CorrectionButton,
            BitePointOutput = Format((profile.BitePoint?.Output ?? .5) * 100),
            BitePointButton = profile.BitePoint?.Button,
            CorrectionRate = Format(profile.Session?.MaximumCorrectionPerSecond ?? 1000),
            CorrectionOutputRate = Format(profile.Session?.MaximumCorrectionOutputPerSecond ?? .05),
            SessionGapMilliseconds = Format((profile.Session?.ResetAfterSeconds ?? .1) * 1000)
        };
    }

    public void SetCurvePoints(IEnumerable<CurvePoint> points)
    {
        var curve = ProfileCurve.PiecewiseLinear(points);
        Points = string.Join("; ", curve.Points.Select(p => $"{Format(p.Input)}:{Format(p.Output)}"));
        CurveKind = "Custom";
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
