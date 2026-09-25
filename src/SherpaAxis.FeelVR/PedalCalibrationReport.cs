using System.Buffers.Binary;
using SherpaAxis.Devices;

namespace SherpaAxis.FeelVR;

public static class PedalCalibrationReport
{
    public const byte ReportId = 3;
    public const int Length = 17;

    public static bool Supports(HidDeviceInfo collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return collection.VendorId == PedalInputReport.VendorId && collection.ProductId == PedalInputReport.ProductId &&
            collection.UsagePage == 0xFF00 && collection.Usage == 1 && collection.FeatureReportLength == Length;
    }

    public static PedalCalibration Decode(ReadOnlySpan<byte> report)
    {
        if (report.Length != Length || report[0] != ReportId)
        {
            throw new InvalidDataException("Expected a 17-byte FEEL-VR calibration report with ID 3.");
        }

        return new PedalCalibration(
            new HardwareCalibrationRange(BinaryPrimitives.ReadInt16LittleEndian(report[1..]), BinaryPrimitives.ReadInt16LittleEndian(report[3..])),
            new HardwareCalibrationRange(BinaryPrimitives.ReadInt32LittleEndian(report[5..]), BinaryPrimitives.ReadInt32LittleEndian(report[9..])),
            new HardwareCalibrationRange(BinaryPrimitives.ReadInt16LittleEndian(report[13..]), BinaryPrimitives.ReadInt16LittleEndian(report[15..])));
    }
}
