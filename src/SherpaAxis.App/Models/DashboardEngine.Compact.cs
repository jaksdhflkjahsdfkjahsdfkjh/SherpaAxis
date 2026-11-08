using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

internal sealed record CompactPedalFrame(string ProfileName, bool Live, ClutchBitePointState BitePoint, IReadOnlyList<AxisFrame> Axes);

public sealed partial class DashboardEngine
{
    internal CompactPedalFrame CompactSnapshot()
    {
        lock (sync)
        {
            return new(activeProfile.Name, CanPublishOutput, BitePointState, Snapshot(PedalAxis.Brake, includeHistory: false).Axes);
        }
    }
}
