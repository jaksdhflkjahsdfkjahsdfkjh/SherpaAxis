using SherpaAxis.FeelVR;

namespace SherpaAxis.Tests;

public sealed class NativePedalProtocolTests
{
    [Fact]
    public void ConfigurationWritesPreserveSignedLayoutAndAllOtherChannels()
    {
        var calibration = new PedalCalibration(new(-1, 16384), new(-2, 16777216), new(-3, 16384));
        var bytes = PedalCalibrationReport.Encode(calibration);
        Assert.Equal("03FFFF0040FEFFFFFF00000001FDFF0040", Convert.ToHexString(bytes));
        Assert.Equal(calibration, PedalCalibrationReport.Decode(bytes));
        var changed = calibration.WithRange(PedalChannel.Brake, new(100, 500));
        Assert.Equal(calibration.Throttle, changed.Throttle);
        Assert.Equal(calibration.Clutch, changed.Clutch);
        Assert.Throws<OverflowException>(() => PedalCalibrationReport.Encode(calibration with { Throttle = new(0, 32768) }));
    }

    [Theory]
    [InlineData(PedalDeviceCommand.LedOff, "0200000000000000000000000000000000")]
    [InlineData(PedalDeviceCommand.LedOn, "0201000000000000000000000000000000")]
    [InlineData(PedalDeviceCommand.EnterBootloader, "0202000000000000000000000000000000")]
    [InlineData(PedalDeviceCommand.SaveConfiguration, "0203000000000000000000000000000000")]
    public void CommandsIncludeIdAndCollectionPadding(PedalDeviceCommand command, string expected) =>
        Assert.Equal(expected, Convert.ToHexString(PedalCommandReport.Encode(command)));

    [Fact]
    public void HardwareDeadzonesTrimCapturedIntegerBoundsAndRejectCollapsedRanges()
    {
        Assert.Equal(new(120, 480), PedalHardwareSettings.ApplyDeadzones(new(100, 500), 5, 5));
        Assert.Equal(new(124, 475), PedalHardwareSettings.ApplyDeadzones(new(103, 497), 5.5m, 5.5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => PedalHardwareSettings.ApplyDeadzones(new(100, 500), 21, 0));
        Assert.Throws<ArgumentException>(() => PedalHardwareSettings.ApplyDeadzones(new(100, 100), 0, 0));
        Assert.Throws<ArgumentException>(() => PedalHardwareSettings.Validate(PedalHardwareSettings.Defaults with { Brake = new(20, 10) }));
    }

    [Theory]
    [InlineData("PE001700", "AA-00")]
    [InlineData("PE0017FF", "JP-FF")]
    public void DeviceIdentitySeparatesFirmwareAndFormatsTheSerialChecksum(string raw, string serial)
    {
        var identity = PedalDeviceIdentity.Parse(raw);
        Assert.Equal("PE0", identity.Hardware);
        Assert.Equal("017", identity.Firmware);
        Assert.Equal(serial, identity.Serial);
        Assert.True(PedalDeviceIdentity.SameController(raw, raw.Replace("017", "018", StringComparison.Ordinal)));
        Assert.False(PedalDeviceIdentity.SameController(raw, raw + "00"));
    }
}
