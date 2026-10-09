using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public static class StarterProfiles
{
    public static IReadOnlyList<PedalProfile> Create() =>
    [
        Create("Linear baseline", "Linear", "Linear", "Linear"),
        Create("Precision control", "Gentle progressive", "Progressive", "S curve", SignalFilterKind.Exponential),
        Create("Responsive control", "Gentle aggressive", "Early bite", "Linear", SignalFilterKind.Median),
        Create("Smooth control", "S curve", "Late bite", "S curve", SignalFilterKind.MovingAverage)
    ];

    private static PedalProfile Create(string name, string throttle, string brake, string clutch, SignalFilterKind? filter = null)
    {
        string[] curves = [throttle, brake, clutch];
        return new(name, Enum.GetValues<PedalAxis>().Select((axis, index) => (axis, profile: new AxisProfile(
            new(0, DashboardEngine.DeviceMaximum(axis)), curve: CurvePresets.Create(curves[index]),
            filter: filter.HasValue ? new(.01, .1, filter.Value, 5) : null,
            session: new(1000, .05)))).ToDictionary(pair => pair.axis, pair => pair.profile));
    }
}
