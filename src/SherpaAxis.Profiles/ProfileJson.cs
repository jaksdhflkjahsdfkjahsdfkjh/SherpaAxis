using System.Text.Json;
using System.Text.Json.Serialization;
using SherpaAxis.Core;

namespace SherpaAxis.Profiles;

public static class ProfileJson
{
    public const int SchemaVersion = 1;
    public const int MaximumFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true
    };

    public static PedalProfile Deserialize(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Profile exceeds the one MiB size limit.");
        }

        try
        {
            var document = JsonSerializer.Deserialize<ProfileDocument>(json, Options)
                ?? throw new InvalidDataException("Expected a profile object.");
            if (document.SchemaVersion != SchemaVersion)
            {
                throw new InvalidDataException($"Unsupported profile schema version {document.SchemaVersion}; expected {SchemaVersion}.");
            }

            var axes = new Dictionary<PedalAxis, AxisProfile>();
            foreach (var (name, settings) in document.Axes)
            {
                var axis = ParseAxis(name);
                if (settings is null)
                {
                    throw new InvalidDataException($"Missing settings for {name}.");
                }

                axes.Add(axis, new AxisProfile(
                    new AxisCalibration(settings.Calibration.Minimum, settings.Calibration.Maximum, settings.Calibration.Inverted),
                    new AxisDeadzone(settings.Deadzone.Lower, settings.Deadzone.Upper),
                    ReadCurve(settings.Curve), settings.MaximumBaselineCorrection,
                    settings.Filter is { } filter ? new LowPassFilterSettings(filter.TimeConstantSeconds, filter.ResetAfterSeconds) : null));
            }

            return new PedalProfile(document.Name, axes);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException($"Invalid profile: {exception.Message}", exception);
        }
    }

    public static byte[] Serialize(PedalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var document = new ProfileDocument
        {
            SchemaVersion = SchemaVersion,
            Name = profile.Name,
            Axes = profile.Axes.OrderBy(pair => pair.Key).ToDictionary(
                pair => AxisName(pair.Key), pair => ToDocument(pair.Value), StringComparer.Ordinal)
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        if (json.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Profile exceeds the one MiB size limit.");
        }

        return json;
    }

    public static PedalAxis ParseAxis(string name) => name switch
    {
        "throttle" => PedalAxis.Throttle,
        "brake" => PedalAxis.Brake,
        "clutch" => PedalAxis.Clutch,
        _ => throw new ArgumentException($"Unknown pedal axis: {name}", nameof(name))
    };

    public static string AxisName(PedalAxis axis) => axis switch
    {
        PedalAxis.Throttle => "throttle",
        PedalAxis.Brake => "brake",
        PedalAxis.Clutch => "clutch",
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };

    private static ProfileCurve ReadCurve(CurveDocument curve) => curve.Kind switch
    {
        "linear" when curve.Exponent is null && curve.Points is null => ProfileCurve.Linear,
        "power" when curve.Exponent.HasValue && curve.Points is null => ProfileCurve.Power(curve.Exponent.Value),
        "piecewiseLinear" when curve.Exponent is null && curve.Points is not null =>
            ProfileCurve.PiecewiseLinear(curve.Points.Select(point => point is null
                ? throw new InvalidDataException("Curve points cannot be null.") : point.ToPoint())),
        _ => throw new InvalidDataException("Unknown curve kind or conflicting curve fields.")
    };

    private static AxisDocument ToDocument(AxisProfile axis) => new()
    {
        Calibration = new() { Minimum = axis.Calibration.Minimum, Maximum = axis.Calibration.Maximum, Inverted = axis.Calibration.Inverted },
        Deadzone = new() { Lower = axis.Deadzone.Lower, Upper = axis.Deadzone.Upper },
        Curve = new()
        {
            Kind = axis.Curve.Kind,
            Exponent = axis.Curve.Exponent,
            Points = axis.Curve.Kind == "piecewiseLinear" ? axis.Curve.Points.Select(point =>
                new CurvePointDocument { Input = point.Input, Output = point.Output }).ToArray() : null
        },
        Filter = axis.Filter is { } filter ? new() { TimeConstantSeconds = filter.TimeConstantSeconds, ResetAfterSeconds = filter.ResetAfterSeconds } : null,
        MaximumBaselineCorrection = axis.MaximumBaselineCorrection
    };
}
