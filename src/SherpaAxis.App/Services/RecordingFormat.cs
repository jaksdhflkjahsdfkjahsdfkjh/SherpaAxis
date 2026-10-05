using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SherpaAxis.App.Models;
using SherpaAxis.Core;

namespace SherpaAxis.App.Services;

internal static class RecordingFormat
{
    private static readonly string[] Axes = ["throttle", "brake", "clutch"];
    private static readonly string[] Stages = ["input", "baseline_correction", "target_correction", "corrected_input", "normalized", "corrected_normalized",
        "filtered_input", "filtered_normalized", "after_deadzone", "output", "filter_reset", "release_confirmed",
        "correction_suspended", "correction_output_delta", "session_reset"];
    public const string InputHeader = "elapsed_seconds,connection,throttle_device_axis,brake_device_axis,clutch_device_axis";
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string ProcessedHeader { get; } = "sequence,configuration_revision,elapsed_seconds,connection," +
        string.Join(',', Axes.SelectMany(axis => Stages.Select(stage => axis + "_" + stage)));

    public static string Input(RecordedSample sample) => FormattableString.Invariant(
        $"{sample.ElapsedSeconds:F7},{sample.Connection},{sample.Input.Throttle},{sample.Input.Brake},{sample.Input.Clutch}");

    public static string Processed(long sequence, RecordedSample sample)
    {
        var line = new StringBuilder(FormattableString.Invariant($"{sequence},{sample.ConfigurationRevision},{sample.ElapsedSeconds:F7},{sample.Connection}"));
        Append(sample.Throttle);
        Append(sample.Brake);
        Append(sample.Clutch);
        return line.ToString();

        void Append(AxisSessionResult result)
        {
            var value = result.Processing;
            line.Append(CultureInfo.InvariantCulture,
                $",{value.Input:R},{value.BaselineCorrection:R},{result.TargetCorrection:R},{value.CorrectedInput:R},{value.Normalized:R},{value.CorrectedNormalized:R},{value.FilteredInput:R},{value.FilteredNormalized:R},{value.AfterDeadzone:R},{value.Output:R},{(value.FilterReset ? 1 : 0)},{(result.ReleaseConfirmed ? 1 : 0)},{(result.CorrectionSuspended ? 1 : 0)},{result.CorrectionOutputDelta:R},{result.ResetReason}");
        }
    }
}
