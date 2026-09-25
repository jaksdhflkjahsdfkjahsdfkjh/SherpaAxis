using SherpaAxis.Devices;

namespace SherpaAxis.FeelVR;

public sealed record CalibrationSnapshot(int SchemaVersion, DateTimeOffset CapturedAtUtc,
    HidDeviceInfo Device, PedalCalibration Calibration);

public sealed class CalibrationSnapshotReader
{
    private readonly IInputReportSource source;
    private readonly IFeatureReportReader featureReader;

    public CalibrationSnapshotReader(IInputReportSource source, IFeatureReportReader featureReader)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(featureReader);
        this.source = source;
        this.featureReader = featureReader;
    }

    public async Task<CalibrationSnapshot> ReadAsync(TimeSpan timeout, string? devicePath = null,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var candidates = source.Find(PedalInputReport.VendorId, PedalInputReport.ProductId)
            .Where(PedalCalibrationReport.Supports)
            .Where(device => devicePath is null || string.Equals(device.Path, devicePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new IOException("No compatible FEEL-VR configuration collection is visible.");
        }

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException("Multiple pedals match. Select the vendor-defined collection path from list using --device.");
        }

        var device = candidates[0];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        byte[] report;
        try
        {
            report = await featureReader.ReadAsync(device, PedalCalibrationReport.ReportId, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Calibration feature read timed out.", exception);
        }

        return new CalibrationSnapshot(1, DateTimeOffset.UtcNow, device, PedalCalibrationReport.Decode(report));
    }
}
