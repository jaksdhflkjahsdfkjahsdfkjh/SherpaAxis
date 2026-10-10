using System.Runtime.Versioning;

namespace SherpaAxis.Devices;

[SupportedOSPlatform("windows")]
public sealed class WindowsHidFeatureWriter : IFeatureReportWriter
{
    public async Task WriteAsync(HidDeviceInfo collection, ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (report.IsEmpty || report.Length != collection.FeatureReportLength)
        {
            throw new ArgumentException("Supply the complete padded HID feature report, including its ID.", nameof(report));
        }
        _ = await WindowsHidFeatureTransfer.RunAsync(collection, report.ToArray(), true, cancellationToken).ConfigureAwait(false);
    }
}
