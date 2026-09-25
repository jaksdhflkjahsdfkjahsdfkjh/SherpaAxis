namespace SherpaAxis.FeelVR;

public sealed record HardwareCalibrationRange(int Minimum, int Maximum)
{
    public bool HasUsableRange => Maximum > Minimum;
}

public sealed record PedalCalibration(
    HardwareCalibrationRange Throttle,
    HardwareCalibrationRange Brake,
    HardwareCalibrationRange Clutch);
