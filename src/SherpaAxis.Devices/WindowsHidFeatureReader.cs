using System.Runtime.Versioning;

namespace SherpaAxis.Devices;

[SupportedOSPlatform("windows")]
public sealed class WindowsHidFeatureReader : IFeatureReportReader
{
    public Task<byte[]> ReadAsync(HidDeviceInfo collection, byte reportId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.FeatureReportLength == 0) { throw new ArgumentException("Collection has no feature reports.", nameof(collection)); }
        var buffer = new byte[collection.FeatureReportLength];
        buffer[0] = reportId;
        return WindowsHidFeatureTransfer.RunAsync(collection, buffer, false, cancellationToken);
    }
}
