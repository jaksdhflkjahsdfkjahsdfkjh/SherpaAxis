namespace SherpaAxis.App.Models;

public sealed partial class DashboardEngine
{
    private long? gameProfileReleasedSince;
    private long? gameProfileLastSample;
    private bool HasReleasedGameProfileInput => sourceActive && source.Kind == "live-device-axis" && calibration is not { IsActive: true }
        && !bitePoint.Holding && axes.Values.All(axis => IsFresh(axis) && axis.Current is { } current
            && current.Processing.CorrectedNormalized <= .02 && current.Processing.FilteredNormalized <= .02
            && !axis.ManualCapture && !axis.ManualAdjusting);

    private void ObserveReleasedPedalsForProfiles()
    {
        var released = HasReleasedGameProfileInput;
        if (gameProfileLastSample is not { } last || clock.GetElapsedTime(last) >= TimeSpan.FromMilliseconds(250) || !released)
        { gameProfileReleasedSince = null; }
        gameProfileLastSample = clock.GetTimestamp();
        if (released) { gameProfileReleasedSince ??= gameProfileLastSample; }
    }

    internal bool PedalsReleasedForProfileSwitch
    {
        get
        {
            lock (sync)
            {
                return HasReleasedGameProfileInput && gameProfileReleasedSince is { } since
                    && clock.GetElapsedTime(since) >= TimeSpan.FromMilliseconds(300);
            }
        }
    }

    internal bool SwitchProfileWhenReleased(Action switchProfile)
    {
        lock (sync)
        {
            if (!PedalsReleasedForProfileSwitch) { return false; }
            switchProfile();
            return true;
        }
    }
}
