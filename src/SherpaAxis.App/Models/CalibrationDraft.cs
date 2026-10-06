using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed class CalibrationDraft(PedalAxis axis, long connection, double released)
{
    public PedalAxis Axis { get; } = axis;
    public long Connection { get; } = connection;
    public double Released { get; } = released;

    public AxisCalibration Complete(long currentConnection, double pressed)
    {
        if (currentConnection != Connection) { throw new InvalidOperationException("Connection changed. Restart calibration with the pedal released."); }
        return new(Math.Min(Released, pressed), Math.Max(Released, pressed), pressed < Released);
    }
}
