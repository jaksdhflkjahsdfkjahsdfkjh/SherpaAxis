namespace SherpaAxis.Devices;

public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort Version,
    ushort UsagePage,
    ushort Usage,
    ushort InputReportLength,
    ushort OutputReportLength,
    ushort FeatureReportLength);
