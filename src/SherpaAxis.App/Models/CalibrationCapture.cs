using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed record CalibrationCapture(PedalAxis Axis, int Connection, double Minimum, double Maximum,
    double Current, long SampleCount, string? Failure = null)
{
    public bool IsActive => Failure is null;

    public CalibrationCapture Include(double input) => this with
    {
        Minimum = SampleCount == 0 ? input : Math.Min(Minimum, input),
        Maximum = SampleCount == 0 ? input : Math.Max(Maximum, input),
        Current = input,
        SampleCount = SampleCount + 1
    };
}
