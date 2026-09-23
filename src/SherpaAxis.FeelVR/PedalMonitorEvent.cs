namespace SherpaAxis.FeelVR;

public enum PedalConnectionState
{
    Waiting,
    Connected,
    Disconnected
}

public sealed record PedalMonitorEvent(
    PedalConnectionState State,
    TimeSpan Elapsed,
    int ConnectionNumber,
    PedalAxes? Axes = null,
    string? Detail = null);
