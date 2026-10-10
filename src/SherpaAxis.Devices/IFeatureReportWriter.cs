namespace SherpaAxis.Devices;

public interface IFeatureReportWriter
{
    Task WriteAsync(HidDeviceInfo collection, ReadOnlyMemory<byte> report, CancellationToken cancellationToken);
}
