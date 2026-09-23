namespace SherpaAxis.FeelVR;

// Firmware-calibrated HID axes; these are not ADC RAW samples.
public readonly record struct PedalAxes(int Throttle, int Brake, int Clutch);
