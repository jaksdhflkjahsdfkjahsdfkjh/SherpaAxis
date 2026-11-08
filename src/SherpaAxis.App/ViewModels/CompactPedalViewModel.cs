using SherpaAxis.App.Models;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

internal sealed class CompactPedalViewModel(PedalOverlaySettingsViewModel settings) : ObservableObject
{
    public PedalOverlaySettingsViewModel Settings { get; } = settings;
    public IReadOnlyList<AxisCardViewModel> Cards { get; } = new[] { PedalAxis.Clutch, PedalAxis.Brake, PedalAxis.Throttle }.Select(axis => new AxisCardViewModel(axis)).ToArray();
    private string profileName = "";
    public string ProfileName { get => profileName; private set => Set(ref profileName, value); }
    private bool isLive;
    public bool IsLive { get => isLive; private set => Set(ref isLive, value); }
    private string status = "Waiting for pedal input";
    public string Status { get => status; private set => Set(ref status, value); }
    private string bitePointInfo = "Bite point: off";
    public string BitePointInfo { get => bitePointInfo; private set => Set(ref bitePointInfo, value); }
    private TelemetryFrame? graphFrame;
    public TelemetryFrame? GraphFrame { get => graphFrame; internal set => Set(ref graphFrame, value); }

    internal void Refresh(CompactPedalFrame frame, string? pauseReason)
    {
        ProfileName = frame.ProfileName;
        IsLive = frame.Live && pauseReason is null;
        Status = pauseReason ?? (IsLive ? "Live" : "Waiting for pedal input");
        foreach (var card in Cards)
        {
            var axis = frame.Axes.Single(item => item.Axis == card.Axis);
            card.Update(IsLive && axis.IsFresh ? axis : axis with { Result = null, FinalOutput = 0, IsFresh = false });
        }
        var bite = frame.BitePoint;
        BitePointInfo = !bite.Enabled ? "Bite point: off" : !IsLive ? "Bite point: paused"
            : FormattableString.Invariant($"Bite point: {bite.Output * 100:0}% · {(bite.Mode == ClutchBitePointMode.HoldFullRange ? bite.Holding ? "Full range" : "Limited range" : bite.Holding ? "Holding" : "Ready")}");
    }
}
