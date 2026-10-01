using SherpaAxis.Cli;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("""
        SherpaAxis list
        SherpaAxis monitor [--seconds 1..3600] [--csv new-file.csv] [--device HID-path]
        SherpaAxis snapshot [--json new-file.json] [--device vendor-HID-path]
        SherpaAxis replay --input capture.csv --csv new-file.csv [--axis brake|throttle|clutch]
          (--profile profile.json OR --minimum value --maximum value [processing options])
          [--baseline released-value] [--from seconds] [--to seconds]
        Processing options: [--invert true|false] [--max-correction counts] [--deadzone-low fraction]
          [--deadzone-high fraction] [--exponent value] [--filter-ms time-constant] [--filter-reset-ms gap]
        SherpaAxis profile create --json new.json --name name --axis brake|throttle|clutch
          --minimum value --maximum value [processing options] [--overwrite true|false]
        SherpaAxis profile validate --input profile.json
        SherpaAxis profile set-axis --input profile.json --json new.json --axis brake|throttle|clutch
          --minimum value --maximum value [processing options] [--overwrite true|false]
        SherpaAxis profile export --input profile.json --json new.json [--name name] [--overwrite true|false]
        Without --profile, replay --baseline also requires --max-correction.
        """);
    return args.Length == 0 ? 1 : 0;
}

try
{
    if (args[0] == "replay")
    {
        await ReplayCommand.ExecuteAsync(ReplayOptions.Parse(args[1..])).ConfigureAwait(false);
        return 0;
    }

    if (args[0] == "profile")
    {
        await ProfileCommand.ExecuteAsync(args[1..]).ConfigureAwait(false);
        return 0;
    }

    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("HID monitoring requires Windows.");
        return 1;
    }

    var source = new WindowsHidInputSource();
    if (args is ["list"])
    {
        var collections = source.Find(PedalInputReport.VendorId, PedalInputReport.ProductId);
        foreach (var collection in collections)
        {
            Console.WriteLine($"{collection.VendorId:X4}:{collection.ProductId:X4} version={collection.Version:X4} " +
                $"usage={collection.UsagePage:X4}:{collection.Usage:X4} " +
                $"input={collection.InputReportLength} output={collection.OutputReportLength} feature={collection.FeatureReportLength}");
            Console.WriteLine(collection.Path);
        }

        return collections.Count == 0 ? 2 : 0;
    }

    if (args[0] == "snapshot")
    {
        await CalibrationSnapshotCommand.RunAsync(new CalibrationSnapshotReader(source, new WindowsHidFeatureReader()), args[1..]).ConfigureAwait(false);
        return 0;
    }

    if (args[0] != "monitor")
    {
        throw new ArgumentException("Expected list, monitor, snapshot, replay, or profile. Use --help for syntax.");
    }

    var options = MonitorOptions.Parse(args[1..]);
    using var duration = new CancellationTokenSource(options.Duration);
    var interrupted = false;
    ConsoleCancelEventHandler onCancel = (_, cancel) =>
    {
        cancel.Cancel = true;
        interrupted = true;
        duration.Cancel();
    };
    Console.CancelKeyPress += onCancel;
    try
    {
        var monitor = new PedalMonitor(source, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        var result = await AxisCapture.RunAsync(monitor, options, duration.Token).ConfigureAwait(false);
        return result.Error is not null ? 1 : interrupted ? 130 : result.AcquiredSamples == 0 ? 2 : 0;
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
