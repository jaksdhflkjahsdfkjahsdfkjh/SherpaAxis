using System.Text.Json;
using System.Text.Json.Serialization;
using SherpaAxis.Core;

namespace SherpaAxis.Profiles;

public static class ProfileJson
{
    public const int SchemaVersion = 5;
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
            if (document.SchemaVersion is not (1 or 2 or 3 or 4 or SchemaVersion))
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
                if (document.SchemaVersion == 1 && settings.Session is not null)
                {
                    throw new InvalidDataException("Schema 1 does not store session transition limits.");
                }
                if (document.SchemaVersion < 3 && settings.Correction is not null)
                { throw new InvalidDataException("Correction modes require profile schema 3."); }
                if (document.SchemaVersion < 4 && settings.BitePoint is not null)
                { throw new InvalidDataException("Clutch bite point requires profile schema 4."); }
                if (document.SchemaVersion < 5 && settings.BitePoint?.Mode is not null)
                { throw new InvalidDataException("Clutch bite point modes require profile schema 5."); }
                var mode = settings.Correction is { } correction
                    && Enum.TryParse<BaselineCorrectionMode>(correction.Mode, out var parsed) && Enum.IsDefined(parsed)
                    && parsed.ToString() == correction.Mode ? parsed
                    : settings.Correction is null ? BaselineCorrectionMode.Off : throw new InvalidDataException("Unknown correction mode.");

                axes.Add(axis, new AxisProfile(
                    new AxisCalibration(settings.Calibration.Minimum, settings.Calibration.Maximum, settings.Calibration.Inverted),
                    new AxisDeadzone(settings.Deadzone.Lower, settings.Deadzone.Upper),
                    ReadCurve(settings.Curve), settings.MaximumBaselineCorrection,
                    settings.Filter is { } filter ? ReadFilter(filter, document.SchemaVersion) : null,
                    settings.Session is { } session ? new AxisSessionSettings(session.MaximumCorrectionPerSecond,
                        session.MaximumCorrectionOutputPerSecond, session.ResetAfterSeconds) : null,
                    mode, settings.Correction?.Button, settings.BitePoint is { } bite ? new(bite.Output, bite.Button, ReadBitePointMode(bite.Mode)) : null));
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
        Session = axis.Session is { } session ? new()
        {
            MaximumCorrectionPerSecond = session.MaximumCorrectionPerSecond,
            MaximumCorrectionOutputPerSecond = session.MaximumCorrectionOutputPerSecond,
            ResetAfterSeconds = session.ResetAfterSeconds
        } : null,
        Calibration = new() { Minimum = axis.Calibration.Minimum, Maximum = axis.Calibration.Maximum, Inverted = axis.Calibration.Inverted },
        Deadzone = new() { Lower = axis.Deadzone.Lower, Upper = axis.Deadzone.Upper },
        Curve = new()
        {
            Kind = axis.Curve.Kind,
            Exponent = axis.Curve.Exponent,
            Points = axis.Curve.Kind == "piecewiseLinear" ? axis.Curve.Points.Select(point =>
                new CurvePointDocument { Input = point.Input, Output = point.Output }).ToArray() : null
        },
        Filter = axis.Filter is { } filter ? new()
        {
            TimeConstantSeconds = filter.TimeConstantSeconds,
            ResetAfterSeconds = filter.ResetAfterSeconds,
            Kind = filter.Kind.ToString(),
            WindowSamples = filter.WindowSamples
        } : null,
        MaximumBaselineCorrection = axis.MaximumBaselineCorrection,
        Correction = axis.CorrectionMode != BaselineCorrectionMode.Off || axis.CorrectionButton is not null
            ? new() { Mode = axis.CorrectionMode.ToString(), Button = axis.CorrectionButton } : null,
        BitePoint = axis.BitePoint is { } bite ? new() { Output = bite.Output, Button = bite.Button, Mode = bite.Mode.ToString() } : null
    };

    private static ClutchBitePointMode ReadBitePointMode(string? mode) => mode is null ? ClutchBitePointMode.HoldMinimum
        : Enum.TryParse<ClutchBitePointMode>(mode, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == mode
            ? parsed : throw new InvalidDataException("Unknown clutch bite point mode.");

    private static LowPassFilterSettings ReadFilter(FilterDocument filter, int version)
    {
        if (version == 1)
        {
            if (filter.Kind is not null || filter.WindowSamples is not null)
            {
                throw new InvalidDataException("Schema 1 supports only the original exponential filter fields.");
            }
            return new(filter.TimeConstantSeconds, filter.ResetAfterSeconds);
        }
        if (filter.Kind is null || !Enum.TryParse<SignalFilterKind>(filter.Kind, out var kind)
            || !Enum.IsDefined(kind) || kind.ToString() != filter.Kind || filter.WindowSamples is null)
        {
            throw new InvalidDataException("Schema 2 filters require a supported kind and windowSamples.");
        }
        return new(filter.TimeConstantSeconds, filter.ResetAfterSeconds, kind, filter.WindowSamples.Value);
    }
}
