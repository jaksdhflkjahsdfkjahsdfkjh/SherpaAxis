using System.Buffers.Binary;
using SherpaAxis.Devices;

namespace SherpaAxis.FeelVR;

public static class PedalInputReport
{
    public const ushort VendorId = 0x0483;
    public const ushort ProductId = 0xA2EA;
    public const int Length = 9;
    public const int ThrottleClutchMaximum = 16_384;
    public const int BrakeMaximum = 16_777_216;

    public static bool Supports(HidDeviceInfo collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return collection.VendorId == VendorId && collection.ProductId == ProductId &&
            collection.UsagePage == 1 && collection.Usage == 4 &&
            collection.InputReportLength == Length;
    }

    public static PedalAxes Decode(ReadOnlySpan<byte> report)
    {
        if (report.Length != Length || report[0] != 1)
        {
            throw new InvalidDataException("Expected a nine-byte FEEL-VR joystick report with ID 1.");
        }

        var throttle = BinaryPrimitives.ReadUInt16LittleEndian(report[1..]);
        var brake = BinaryPrimitives.ReadUInt32LittleEndian(report[3..]);
        var clutch = BinaryPrimitives.ReadUInt16LittleEndian(report[7..]);
        if (throttle > ThrottleClutchMaximum || brake > BrakeMaximum || clutch > ThrottleClutchMaximum)
        {
            throw new InvalidDataException("Pedal report exceeds the verified HID logical ranges.");
        }

        return new PedalAxes(throttle, (int)brake, clutch);
    }
}
