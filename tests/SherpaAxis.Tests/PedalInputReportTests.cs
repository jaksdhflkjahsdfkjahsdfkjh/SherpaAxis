using System.Buffers.Binary;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Tests;

public sealed class PedalInputReportTests
{
    [Fact]
    public void DecodesIndependentFieldsWithoutLosingBrakePrecision()
    {
        var report = Frame(1234, 3_337_473, 5678);
        Assert.Equal(new PedalAxes(1234, 3_337_473, 5678), PedalInputReport.Decode(report));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(16_384, 16_777_216, 16_384)]
    public void AcceptsInclusiveLogicalBounds(int throttle, int brake, int clutch) =>
        Assert.Equal(new PedalAxes(throttle, brake, clutch), PedalInputReport.Decode(Frame(throttle, brake, clutch)));

    [Theory]
    [InlineData(16_385, 0, 0)]
    [InlineData(0, 16_777_217, 0)]
    [InlineData(0, 0, 16_385)]
    [InlineData(0, -1, 0)]
    public void RejectsOutOfRangePayloads(int throttle, int brake, int clutch) =>
        Assert.Throws<InvalidDataException>(() => PedalInputReport.Decode(Frame(throttle, brake, clutch)));

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(17)]
    public void RejectsWrongLengths(int length) =>
        Assert.Throws<InvalidDataException>(() => PedalInputReport.Decode(new byte[length]));

    [Fact]
    public void RejectsFeatureReportsInsteadOfInterpretingThemAsInput()
    {
        var report = Frame(0, 0, 0);
        report[0] = 3;
        Assert.Throws<InvalidDataException>(() => PedalInputReport.Decode(report));
    }

    [Fact]
    public void SelectsOnlyTheVerifiedJoystickCollection()
    {
        var joystick = Collection;
        Assert.True(PedalInputReport.Supports(joystick));
        Assert.False(PedalInputReport.Supports(joystick with { UsagePage = 0xFF00 }));
        Assert.False(PedalInputReport.Supports(joystick with { ProductId = 0xA2E9 }));
        Assert.False(PedalInputReport.Supports(joystick with { VendorId = 1 }));
        Assert.False(PedalInputReport.Supports(joystick with { Usage = 5 }));
        Assert.False(PedalInputReport.Supports(joystick with { InputReportLength = 8 }));
    }

    internal static HidDeviceInfo Collection => new("test-pedals", 0x0483, 0xA2EA, 0x0200, 1, 4, 9, 0, 0);

    internal static byte[] Frame(int throttle, int brake, int clutch)
    {
        var report = new byte[9];
        report[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(1), (ushort)throttle);
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(3), unchecked((uint)brake));
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(7), (ushort)clutch);
        return report;
    }
}
