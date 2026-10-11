using SherpaAxis.Devices;

namespace SherpaAxis.FeelVR;

public sealed record HardwareRecoverySnapshot(int SchemaVersion, DateTimeOffset CreatedAtUtc, HidDeviceInfo Device,
    PedalCalibration Saved, PedalCalibration LastConfirmed, PedalCalibration? Attempted, bool SaveUncertain = false, string? SerialNumber = null,
    bool IsManualBackup = false);

public interface IHardwareRecoveryStore
{
    Task WriteAsync(HardwareRecoverySnapshot snapshot, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}
