using System.Text.Json;
using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public interface IDashboardRecording
{
    // Called under the processing lock; implementations must enqueue without waiting for storage.
    void RecordSample(RecordedSample sample);
    void RecordEvent(RecordedEvent item);
    void Complete(string reason);
}

public sealed record RecordedSample(double ElapsedSeconds, int Connection, long ConfigurationRevision,
    PedalAxes Input, AxisSessionResult Throttle, AxisSessionResult Brake, AxisSessionResult Clutch);

public sealed record RecordedEvent(string Kind, double ElapsedSeconds, long ConfigurationRevision, object? Details = null);

public sealed record RecordingSource(string Kind, string? CaptureFileName = null, double? PlaybackSpeed = null);

public sealed record RecordedAxisLimits(PedalAxis Axis, double MaximumCorrectionPerSecond,
    double MaximumCorrectionOutputPerSecond, double ResetAfterSeconds);

public sealed record RecordedConfiguration(JsonElement Profile, IReadOnlyList<RecordedAxisLimits> SessionLimits);

public sealed record RecordedHardware(DateTimeOffset CapturedAtUtc, PedalCalibration Calibration,
    int ConnectionAtRead = 0, string Scope = "currentlyConnectedDevice");
