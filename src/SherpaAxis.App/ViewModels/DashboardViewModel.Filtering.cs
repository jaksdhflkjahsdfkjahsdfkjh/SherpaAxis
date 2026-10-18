using System.Collections.ObjectModel;
using System.Globalization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
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
    public string FilterApplyInfo => HasFilterDraft ? "Filter draft · Apply settings to update" : "Filter settings applied";
    public string FilterPreset => FilterPresets.Identify(Editor);
    public string SelectedFilterKind => Editor.FilterEnabled ? Editor.FilterKind : "Off";
    public bool UsesWindowFilter => Editor.FilterKind is "MovingAverage" or "Median" or "WeightedMovingAverage" or "MedianExponential";
    public bool UsesTimeFilter => Editor.FilterKind is "Exponential" or "TwoStageExponential" or "MedianExponential";
    public string FilterInfo => !Editor.FilterEnabled ? "Filtering is off. Choose Sharp, Balanced or Smooth, or select an algorithm to enable it." : Editor.FilterKind switch
    {
        "Median" => "Removes isolated spikes. Larger windows reject longer spikes and add delay.",
        "MovingAverage" => "Averages recent reports. Larger windows smooth more and react more slowly.",
        "WeightedMovingAverage" => "Averages recent reports with more weight on the newest samples.",
        "MedianExponential" => "Rejects spikes first, then smooths the result. Uses both window and time constant.",
        "TwoStageExponential" => "Two smoothing stages. Smooth transitions with more delay than one stage.",
        _ => "One smoothing stage. A larger time constant reduces noise and slows response."
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
        ShowMessage($"{SelectedAxis}: {name} filter in draft. Apply settings to see it in the graph.");
    }

    public void NotifyFilterSettings()
    {
        Changed(nameof(FilterPreset));
        Changed(nameof(SelectedFilterKind));
        Changed(nameof(UsesWindowFilter));
        Changed(nameof(UsesTimeFilter));
        Changed(nameof(FilterInfo));
        Changed(nameof(HasFilterDraft)); Changed(nameof(FilterApplyInfo));
        Changed(nameof(SelectedCorrectionMode)); Changed(nameof(IsButtonCorrection)); Changed(nameof(CorrectionModeHint)); Changed(nameof(CorrectionBindingLabel));
        Changed(nameof(SelectedAutoLoad));
    }

    public static IReadOnlyList<string> CorrectionModes { get; } = ["Off", "Auto", "By button"];
    public string SelectedCorrectionMode => Editor.CorrectionMode switch { BaselineCorrectionMode.Auto => "Auto", BaselineCorrectionMode.ByButton => "By button", _ => "Off" };
    public bool IsButtonCorrection => Editor.CorrectionMode == BaselineCorrectionMode.ByButton;
    public string CorrectionModeHint => Editor.CorrectionMode switch
    {
        BaselineCorrectionMode.Auto => "Captures one stable near-zero baseline after a press and release. Limited to 0.5% of the axis range; active pedal travel is excluded. Hardware-clipped values cannot be recovered.",
        BaselineCorrectionMode.ByButton => "Hold the bound key or controller button only while this pedal is fully released. Adjustment is bounded and stops when you release the button.",
        _ => "Baseline adjustment is off. Hardware calibration remains unchanged."
    };
    public string CorrectionBindingLabel => Editor.CorrectionButton is not { } button ? "No button bound" : button.Kind == "keyboard"
        ? "Key: " + System.Windows.Input.KeyInterop.KeyFromVirtualKey(button.Code) : $"Controller button {button.Code}";
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
        else if (Editor.BuildProfile().MaximumBaselineCorrection == 0)
        {
            var span = Editor.BuildProfile().Calibration.Span;
            Editor.MaximumCorrection = (span * .005).ToString("R", CultureInfo.InvariantCulture);
            Editor.CorrectionRate = (span * .1).ToString("R", CultureInfo.InvariantCulture);
        }
        NotifyFilterSettings(); Apply();
    }
    public void BindCorrection(CorrectionButton button)
    { Editor.CorrectionButton = button; NotifyFilterSettings(); Apply(); }

    public void SetFilterKind(string kind)
    {
        if (kind == "Off") { SetFilterPreset("Off"); return; }
        if (!FilterKinds.Any(choice => choice.Kind == kind)) { throw new ArgumentException("Choose a supported filter.", nameof(kind)); }
        Editor.FilterKind = kind;
        Editor.FilterEnabled = true;
        Changed(nameof(Editor));
        NotifyFilterSettings();
        ShowMessage($"{SelectedAxis}: filter changed in draft. Apply settings to update processing.");
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
