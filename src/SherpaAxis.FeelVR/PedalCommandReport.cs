using System.Buffers.Binary;

namespace SherpaAxis.FeelVR;

public enum PedalDeviceCommand
{
    LedOff = 0,
    LedOn = 1,
    EnterBootloader = 2,
    SaveConfiguration = 3
}

public static class PedalCommandReport
{
    public const byte ReportId = 2;

    public static byte[] Encode(PedalDeviceCommand command)
    {
        if (!Enum.IsDefined(command)) { throw new ArgumentOutOfRangeException(nameof(command)); }
        // The vendor transport pads even the four-byte command payload to the collection's 17 bytes.
        var report = new byte[PedalCalibrationReport.Length];
        report[0] = ReportId;
        BinaryPrimitives.WriteInt32LittleEndian(report.AsSpan(1), (int)command);
        return report;
    }
}
