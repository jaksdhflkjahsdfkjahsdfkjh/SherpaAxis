using SherpaAxis.App.Services.Input;
using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed record ClutchBitePointState(bool Enabled, bool Holding, double Output, ClutchBitePointMode Mode = ClutchBitePointMode.HoldMinimum);

public sealed partial class DashboardEngine
{
    private readonly ClutchBitePointControl bitePoint;
    private byte[]? processingConfiguration;
    private long buttonConnectionRevision;
    private ClutchBitePointSettings? BiteSettings => activeProfile.Axes[PedalAxis.Clutch].BitePoint;
    public ClutchBitePointState BitePointState
    {
        get { lock (sync) { return new(bitePoint.Enabled, bitePoint.Holding && IsFresh(axes[PedalAxis.Clutch]), BiteSettings?.Output ?? .5, BiteSettings?.Mode ?? ClutchBitePointMode.HoldMinimum); } }
    }
    public void SetBitePointEnabled(bool enabled)
    {
        lock (sync)
        {
            bitePoint.SetEnabled(enabled && BiteSettings?.Button is not null);
            PublishBitePointChange();
        }
    }
    private void ProcessBitePointButton(IButtonStateSource buttons)
    {
        var before = (bitePoint.Enabled, bitePoint.Holding);
        var revision = BiteSettings?.Button is { } button ? buttons.GetConnectionRevision(button) : 0;
        if (buttonConnectionRevision != revision)
        {
            buttonConnectionRevision = revision;
            bitePoint.Release();
        }
        if (sourceActive && source.Kind == "live-device-axis" && calibration is not { IsActive: true }
            && IsFresh(axes[PedalAxis.Clutch]) && BiteSettings?.Button is { } binding)
        { bitePoint.Update(buttons.IsDown(binding)); }
        else { bitePoint.Release(); }
        if (before != (bitePoint.Enabled, bitePoint.Holding)) { PublishBitePointChange(); }
    }
    private double FinalOutput(PedalAxis axis, double output)
    {
        if (axis != PedalAxis.Clutch || !bitePoint.Enabled || BiteSettings?.Button is null || !IsFresh(axes[axis])
            || !sourceActive || source.Kind != "live-device-axis" || calibration is { IsActive: true }) { return output; }
        return BiteSettings.Mode == ClutchBitePointMode.HoldFullRange
            ? bitePoint.Holding ? output : output * BiteSettings.Output
            : bitePoint.Holding ? Math.Max(output, BiteSettings.Output) : output;
    }
    private void PublishBitePointChange()
    {
        Event("clutch-bite-point", BitePointState);
        var clutch = axes[PedalAxis.Clutch];
        if (!IsFresh(clutch) || clutch.Current is not { } result) { return; }
        clutch.History.Enqueue(new(elapsed, result.Processing.Normalized, result.Processing.FilteredNormalized,
            FinalOutput(PedalAxis.Clutch, result.Processing.Output), result.Processing.Input, result.Processing.CorrectedNormalized));
        if (clutch.History.Count > historyCapacity) { clutch.History.Dequeue(); }
        if (CanPublishOutput) { output?.Submit(CurrentOutput()); }
    }
    private static byte[] ProcessingConfiguration(PedalProfile profile) => ProfileJson.Serialize(new("Processing",
        profile.Axes.ToDictionary(pair => pair.Key, pair => pair.Value.BitePoint is null ? pair.Value : pair.Value.WithBitePoint(null))));
    private bool SameLimits(IReadOnlyDictionary<PedalAxis, AxisSessionSettings> limits) => activeLimits.Count == limits.Count
        && limits.All(pair => activeLimits.TryGetValue(pair.Key, out var old)
            && old.MaximumCorrectionPerSecond == pair.Value.MaximumCorrectionPerSecond
            && old.MaximumCorrectionOutputPerSecond == pair.Value.MaximumCorrectionOutputPerSecond
            && old.ResetAfterSeconds == pair.Value.ResetAfterSeconds);
}
