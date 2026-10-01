using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.Cli;

internal sealed record ReplayOptions(string InputPath, string OutputPath, string Axis,
    AxisProfile? Configuration, string? ProfilePath, double? Baseline, double From, double To)
{
    public AxisProcessor CreateProcessor() => (Configuration
        ?? throw new InvalidOperationException("Load the replay profile before processing.")).CreateProcessor();

    public async Task<ReplayOptions> ResolveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ProfilePath is null)
        {
            return this;
        }

        var profile = await ProfileStore.LoadAsync(ProfilePath, cancellationToken).ConfigureAwait(false);
        if (!profile.Axes.TryGetValue(ProfileJson.ParseAxis(Axis), out var configuration))
        {
            throw new ArgumentException($"Profile '{profile.Name}' has no {Axis} settings.");
        }

        if (Baseline.HasValue)
        {
            configuration.CreateProcessor().ConfirmReleasedBaseline(Baseline.Value);
        }

        return this with { Configuration = configuration };
    }

    public static ReplayOptions Parse(string[] arguments)
    {
        var options = new OptionValues(arguments, [.. AxisProfileOptions.Names,
            "--input", "--csv", "--axis", "--baseline", "--from", "--to", "--profile"]);
        var axis = options.Get("--axis") ?? "brake";
        _ = ProfileJson.ParseAxis(axis);
        var path = options.Get("--profile");
        if (path is not null && AxisProfileOptions.Names.Any(options.Contains))
        {
            throw new ArgumentException("--profile cannot be combined with processing overrides; edit or export the profile first.");
        }

        if (path is null && options.Contains("--baseline") != options.Contains("--max-correction"))
        {
            throw new ArgumentException("--baseline and --max-correction must be provided together without a profile.");
        }

        var configuration = path is null ? AxisProfileOptions.Parse(options) : null;
        double? baseline = options.Contains("--baseline") ? options.Number("--baseline") : null;
        if (configuration is not null && baseline.HasValue)
        {
            configuration.CreateProcessor().ConfirmReleasedBaseline(baseline.Value);
        }

        var from = options.Number("--from", 0);
        var to = options.Number("--to", double.MaxValue);
        if (from < 0 || to < from)
        {
            throw new ArgumentException("Replay requires 0 <= --from <= --to.");
        }

        return new(options.Required("--input"), options.Required("--csv"), axis, configuration, path, baseline, from, to);
    }
}
