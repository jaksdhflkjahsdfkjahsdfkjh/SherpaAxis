namespace SherpaAxis.Devices;

public interface IFeatureReportReader
{
    Task<byte[]> ReadAsync(HidDeviceInfo collection, byte reportId, CancellationToken cancellationToken);
}
