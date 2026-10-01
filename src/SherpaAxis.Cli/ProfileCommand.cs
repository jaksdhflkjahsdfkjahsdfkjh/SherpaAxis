using SherpaAxis.Profiles;

namespace SherpaAxis.Cli;

internal static class ProfileCommand
{
    public static async Task ExecuteAsync(string[] arguments)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, cancel) =>
        {
            cancel.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var profile = await RunAsync(arguments, cancellation.Token).ConfigureAwait(false);
            Console.WriteLine($"Profile valid: {profile.Name}; schema {ProfileJson.SchemaVersion}; axes: " +
                string.Join(", ", profile.Axes.Keys.Order().Select(ProfileJson.AxisName)));
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    public static async Task<PedalProfile> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments.Length == 0)
        {
            throw new ArgumentException("Expected profile create, validate, set-axis, or export.");
        }

        var operation = arguments[0];
        string[] allowed = operation switch
        {
            "create" => [.. AxisProfileOptions.Names, "--json", "--name", "--axis", "--overwrite"],
            "set-axis" => [.. AxisProfileOptions.Names, "--input", "--json", "--axis", "--overwrite"],
            "validate" => ["--input"],
            "export" => ["--input", "--json", "--name", "--overwrite"],
            _ => throw new ArgumentException("Expected profile create, validate, set-axis, or export.")
        };
        var options = new OptionValues(arguments[1..], allowed);
        var overwrite = options.Boolean("--overwrite");
        var destination = operation == "validate" ? null : options.Required("--json");
        PedalProfile profile;
        if (operation == "create")
        {
            var axis = ProfileJson.ParseAxis(options.Required("--axis"));
            profile = new PedalProfile(options.Required("--name"), new Dictionary<PedalAxis, AxisProfile>
            {
                [axis] = AxisProfileOptions.Parse(options)
            });
        }
        else
        {
            profile = await ProfileStore.LoadAsync(options.Required("--input"), cancellationToken).ConfigureAwait(false);
            if (operation == "set-axis")
            {
                var axes = new Dictionary<PedalAxis, AxisProfile>(profile.Axes)
                {
                    [ProfileJson.ParseAxis(options.Required("--axis"))] = AxisProfileOptions.Parse(options)
                };
                profile = new PedalProfile(profile.Name, axes);
            }
            else if (operation == "export" && options.Get("--name") is { } name)
            {
                profile = new PedalProfile(name, profile.Axes);
            }
        }

        if (destination is not null)
        {
            await ProfileStore.SaveAsync(destination, profile, overwrite, cancellationToken).ConfigureAwait(false);
        }

        return profile;
    }
}
