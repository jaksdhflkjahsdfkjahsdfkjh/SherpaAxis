using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Tests;

public sealed class PedalCalibrationReportTests
{
    [Fact]
    public void DecodesAllSixBoundsFromAnIndependentlyAuthoredReport()
    {
        var report = Convert.FromHexString("03d204e1105634120032547600bc0aef0d");
        var calibration = PedalCalibrationReport.Decode(report);
        Assert.Equal(new HardwareCalibrationRange(1234, 4321), calibration.Throttle);
        Assert.Equal(new HardwareCalibrationRange(0x123456, 0x765432), calibration.Brake);
        Assert.Equal(new HardwareCalibrationRange(2748, 3567), calibration.Clutch);
    }

    [Fact]
    public void PreservesInvalidAndSignedBoundsForDiagnostics()
    {
        var calibration = PedalCalibrationReport.Decode(Convert.FromHexString("03ffff008000000080ffffff7f00000000"));
        Assert.Equal(new HardwareCalibrationRange(-1, short.MinValue), calibration.Throttle);
        Assert.Equal(new HardwareCalibrationRange(int.MinValue, int.MaxValue), calibration.Brake);
        Assert.Equal(new HardwareCalibrationRange(0, 0), calibration.Clutch);
        Assert.False(calibration.Throttle.HasUsableRange);
        Assert.False(calibration.Clutch.HasUsableRange);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(18)]
    public void RejectsIncompleteAndOversizedReports(int length) =>
        Assert.Throws<InvalidDataException>(() => PedalCalibrationReport.Decode(new byte[length]));

    [Fact]
    public void RejectsACommandReportWithTheSameBufferLength()
    {
        var report = new byte[17];
        report[0] = 2;
        Assert.Throws<InvalidDataException>(() => PedalCalibrationReport.Decode(report));
    }

    [Fact]
    public void MatchesOnlyTheVerifiedConfigurationCollection()
    {
        Assert.True(PedalCalibrationReport.Supports(Collection));
        Assert.False(PedalCalibrationReport.Supports(PedalInputReportTests.Collection));
        Assert.False(PedalCalibrationReport.Supports(Collection with { FeatureReportLength = 16 }));
        Assert.False(PedalCalibrationReport.Supports(Collection with { Usage = 2 }));
        Assert.False(PedalCalibrationReport.Supports(Collection with { ProductId = 0xA2E9 }));
        Assert.False(PedalCalibrationReport.Supports(Collection with { VendorId = 1 }));
    }

    internal static HidDeviceInfo Collection => new("test-configuration", 0x0483, 0xA2EA, 0x0200, 0xFF00, 1, 0, 0, 17);
}
