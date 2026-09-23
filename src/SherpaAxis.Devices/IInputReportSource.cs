namespace SherpaAxis.Devices;

public interface IInputReportSource
{
    IReadOnlyList<HidDeviceInfo> Find(ushort vendorId, ushort productId);

    IInputReportConnection Open(HidDeviceInfo collection);
}

public interface IInputReportConnection : IAsyncDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}
