using System.Globalization;
using SherpaAxis.App.Models;
using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.ViewModels;

public sealed partial class DashboardViewModel
{
    public static IReadOnlyList<FilterChoice> FilterKinds { get; } =
    [
        new("Off", "Off / bypass"),
        new("Exponential", "Exponential"), new("TwoStageExponential", "Two-stage exponential"),
        new("MovingAverage", "Moving average"), new("Median", "Median"),
        new("WeightedMovingAverage", "Weighted moving average"), new("MedianExponential", "Median + exponential")
    ];

    public bool HasFilterDraft
    {
        get
        {
            try
            {
                var draft = Editor.BuildProfile().Filter;
                var current = applied.Axes[SelectedAxis].Filter;
                return draft is null ? current is not null : current is null || draft.Kind != current.Kind
                    || draft.TimeConstantSeconds != current.TimeConstantSeconds || draft.WindowSamples != current.WindowSamples
                    || draft.ResetAfterSeconds != current.ResetAfterSeconds;
            }
            catch (ArgumentException) { return true; }
        }
    }
    public string FilterPreset => FilterPresets.Identify(Editor);
    public string SelectedFilterKind => Editor.FilterEnabled ? Editor.FilterKind : "Off";
    public bool UsesWindowFilter => Editor.FilterKind is "MovingAverage" or "Median" or "WeightedMovingAverage" or "MedianExponential";
    public bool UsesTimeFilter => Editor.FilterKind is "Exponential" or "TwoStageExponential" or "MedianExponential";
    public string FilterInfo => !Editor.FilterEnabled ? "Filtering is off. Choose a preset to reduce sensor noise." : Editor.FilterKind switch
    {
        "Median" => "Removes brief spikes. A larger sample window adds delay.",
        "MovingAverage" => "Averages recent samples. More samples give smoother, slower output.",
        "WeightedMovingAverage" => "Smooths recent samples, favouring newer input.",
        "MedianExponential" => "Removes spikes, then smooths the signal.",
        "TwoStageExponential" => "Smooths twice for steadier output, with more delay.",
        _ => "Reduces small fluctuations. More smoothing adds delay."
    };

    public void SetFilterPreset(string name)
    {
        if (name == "Custom") { NotifyFilterSettings(); return; }
        var settings = FilterPresets.Create(name);
        Editor.FilterMilliseconds = ((settings?.TimeConstantSeconds ?? 0) * 1000).ToString("R", CultureInfo.InvariantCulture);
        if (settings is not null)
        {
            Editor.FilterKind = settings.Kind.ToString();
            Editor.FilterWindowSamples = settings.WindowSamples.ToString(CultureInfo.InvariantCulture);
            Editor.FilterGapMilliseconds = (settings.ResetAfterSeconds * 1000).ToString("R", CultureInfo.InvariantCulture);
        }
        Changed(nameof(Editor));
        NotifyFilterSettings();
        RefreshUnsavedState();
    }

    public void NotifyFilterSettings()
    {
        Changed(nameof(FilterPreset));
        Changed(nameof(SelectedFilterKind));
        Changed(nameof(UsesWindowFilter));
        Changed(nameof(UsesTimeFilter));
        Changed(nameof(FilterInfo));
        Changed(nameof(HasFilterDraft));
        Changed(nameof(SelectedCorrectionMode)); Changed(nameof(IsButtonCorrection)); Changed(nameof(CorrectionModeHint)); Changed(nameof(CorrectionBindingLabel)); Changed(nameof(HasCorrectionBinding));
        Changed(nameof(SelectedAutoLoad));
        NotifyBitePointSettings();
    }

    public static IReadOnlyList<string> CorrectionModes { get; } = ["Off", "Auto", "By button"];
    public string SelectedCorrectionMode => Editor.CorrectionMode switch { BaselineCorrectionMode.Auto => "Auto", BaselineCorrectionMode.ByButton => "By button", _ => "Off" };
    public bool IsButtonCorrection => Editor.CorrectionMode == BaselineCorrectionMode.ByButton;
    public bool HasCorrectionBinding => Editor.CorrectionButton is not null;
    public string CorrectionModeHint => Editor.CorrectionMode switch
    {
        BaselineCorrectionMode.Auto => "Calibrate once in SherpaAxis first. Auto checks five matching press/release cycles with stable releases. If the released value rises, use Fix drift. Calibration is not saved automatically.",
        BaselineCorrectionMode.ByButton => "Take your foot off the pedal. Hold the bound button or Fix drift for at least one stable second. Keep holding until the fix finishes; release to stop.",
        _ => "Automatic drift correction is off. Hold Fix drift to correct it manually."
    };
    public string CorrectionBindingLabel => ButtonLabel(Editor.CorrectionButton);
    public void SetCorrectionMode(string mode)
    {
        var selected = mode switch
        {
            "Off" => BaselineCorrectionMode.Off,
            "Auto" => BaselineCorrectionMode.Auto,
            "By button" => BaselineCorrectionMode.ByButton,
            _ => throw new ArgumentException("Choose a supported correction mode.", nameof(mode))
        };
        Editor.CorrectionMode = selected;
        if (selected == BaselineCorrectionMode.Off) { Editor.MaximumCorrection = "0"; }
        else if (Editor.BuildProfile().MaximumBaselineCorrection <= Editor.BuildProfile().Calibration.Span * .005)
        {
            var span = Editor.BuildProfile().Calibration.Span;
            Editor.MaximumCorrection = (span * .25).ToString("R", CultureInfo.InvariantCulture);
            Editor.CorrectionRate = (span * .1).ToString("R", CultureInfo.InvariantCulture);
        }
        NotifyFilterSettings(); RefreshUnsavedState();
    }
    public void BindCorrection(CorrectionButton button)
    { Editor.CorrectionButton = button; NotifyFilterSettings(); RefreshUnsavedState(); RefreshDrift(); }
    public void ClearCorrectionBinding()
    { Editor.CorrectionButton = null; NotifyFilterSettings(); RefreshUnsavedState(); RefreshDrift(); }

    public void SetFilterKind(string kind)
    {
        if (kind == "Off") { SetFilterPreset("Off"); return; }
        if (!FilterKinds.Any(choice => choice.Kind == kind)) { throw new ArgumentException("Choose a supported filter.", nameof(kind)); }
        Editor.FilterKind = kind;
        Editor.FilterEnabled = true;
        Changed(nameof(Editor));
        NotifyFilterSettings();
        RefreshUnsavedState();
    }

    private static string FilterLabel(AxisProfile axis)
    {
        if (axis.Filter is not { } f) { return "Filter off"; }
        var name = FilterKinds.Single(choice => choice.Kind == f.Kind.ToString()).Name;
        var window = f.Kind is SignalFilterKind.Median or SignalFilterKind.MovingAverage or SignalFilterKind.WeightedMovingAverage or SignalFilterKind.MedianExponential;
        var time = f.Kind is SignalFilterKind.Exponential or SignalFilterKind.TwoStageExponential or SignalFilterKind.MedianExponential;
        return name + (window ? $" · {f.WindowSamples} samples" : "") + (time ? FormattableString.Invariant($" · {f.TimeConstantSeconds * 1000:G} ms") : "");
    }

    public static IReadOnlyList<string> FilterTimes { get; } = ["1", "3", "5", "8", "10", "15", "20", "30", "50"];

    public static IReadOnlyList<string> FilterWindows { get; } = ["3", "5", "7", "9", "11", "15", "21", "31"];

    public static IReadOnlyList<string> ResetGaps { get; } = ["50", "100", "200", "500", "1000"];

    public static IReadOnlyList<string> FilterPresetNames => FilterPresets.Names;
}
