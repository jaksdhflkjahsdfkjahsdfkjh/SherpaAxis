using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public readonly record struct PlotSample(double Time, double Input, double Filtered, double Output, double DeviceInput = 0, double PreFilter = double.NaN, bool IsPadding = false);

public sealed record AxisFrame(PedalAxis Axis, AxisSessionResult? Result, bool IsFresh,
    double? ObservedMinimum, double? ObservedMaximum, IReadOnlyList<PlotSample>? History = null, double? FinalOutput = null);

public sealed record PedalHistory(PedalAxis Axis, IReadOnlyList<PlotSample> Samples, string FilterLabel);

public sealed record DashboardFrame(PedalConnectionState State, string? Detail, int Connection,
    long SampleCount, double ElapsedSeconds, double ReportsPerSecond, IReadOnlyList<AxisFrame> Axes,
    IReadOnlyList<PlotSample> History);
