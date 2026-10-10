namespace SherpaAxis.FeelVR;

public enum PedalChannel { Throttle, Brake, Clutch }

public static class PedalHardwareSettings
{
    public static PedalCalibration Defaults { get; } = new(new(0, PedalInputReport.ThrottleClutchMaximum),
        new(0, PedalInputReport.BrakeMaximum), new(0, PedalInputReport.ThrottleClutchMaximum));

    public static HardwareCalibrationRange Range(this PedalCalibration calibration, PedalChannel channel) => channel switch
    {
        PedalChannel.Throttle => calibration.Throttle,
        PedalChannel.Brake => calibration.Brake,
        PedalChannel.Clutch => calibration.Clutch,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    public static PedalCalibration WithRange(this PedalCalibration calibration, PedalChannel channel, HardwareCalibrationRange range) => channel switch
    {
        PedalChannel.Throttle => calibration with { Throttle = range },
        PedalChannel.Brake => calibration with { Brake = range },
        PedalChannel.Clutch => calibration with { Clutch = range },
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    public static HardwareCalibrationRange ApplyDeadzones(HardwareCalibrationRange captured, decimal lowerPercent, decimal upperPercent)
    {
        ArgumentNullException.ThrowIfNull(captured);
        if (!captured.HasUsableRange) { throw new ArgumentException("Capture distinct minimum and maximum readings first.", nameof(captured)); }
        if (lowerPercent is < 0 or > 20 || upperPercent is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(lowerPercent), "Hardware deadzones must be between 0% and 20%.");
        }
        var span = (decimal)captured.Maximum - captured.Minimum;
        var range = new HardwareCalibrationRange((int)(captured.Minimum + span * lowerPercent / 100),
            (int)(captured.Maximum - span * upperPercent / 100));
        if (!range.HasUsableRange) { throw new ArgumentException("Deadzones leave no usable integer range.", nameof(captured)); }
        return range;
    }

    public static void Validate(PedalCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        foreach (var channel in Enum.GetValues<PedalChannel>())
        {
            var range = calibration.Range(channel);
            var maximum = Defaults.Range(channel).Maximum;
            if (range.Minimum < 0 || range.Maximum > maximum || !range.HasUsableRange)
            {
                throw new ArgumentException($"{channel}: hardware bounds must increase within 0..{maximum}.", nameof(calibration));
            }
        }
    }
}
