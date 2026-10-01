using System.Text.Json.Serialization;
using SherpaAxis.Core;

namespace SherpaAxis.Profiles;

internal sealed class ProfileDocument
{
    public required int SchemaVersion { get; init; }
    public required string Name { get; init; }
    public required Dictionary<string, AxisDocument> Axes { get; init; }
}

internal sealed class AxisDocument
{
    public required CalibrationDocument Calibration { get; init; }
    public required DeadzoneDocument Deadzone { get; init; }
    public required CurveDocument Curve { get; init; }
    public required FilterDocument? Filter { get; init; }
    public required double MaximumBaselineCorrection { get; init; }
}

internal sealed class CalibrationDocument
{
    public required double Minimum { get; init; }
    public required double Maximum { get; init; }
    public required bool Inverted { get; init; }
}

internal sealed class DeadzoneDocument
{
    public required double Lower { get; init; }
    public required double Upper { get; init; }
}

internal sealed class CurveDocument
{
    public required string Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Exponent { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CurvePointDocument[]? Points { get; init; }
}

internal sealed class CurvePointDocument
{
    public required double Input { get; init; }
    public required double Output { get; init; }

    public CurvePoint ToPoint() => new(Input, Output);
}

internal sealed class FilterDocument
{
    public required double TimeConstantSeconds { get; init; }
    public required double ResetAfterSeconds { get; init; }
}
