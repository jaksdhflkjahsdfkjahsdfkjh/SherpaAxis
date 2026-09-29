using SherpaAxis.Cli;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("SherpaAxis list\nSherpaAxis monitor [--seconds 1..3600] [--csv new-file.csv] [--device HID-path]\nSherpaAxis snapshot [--json new-file.json] [--device vendor-HID-path]\nSherpaAxis replay --input capture.csv --csv new-file.csv --minimum value --maximum value [--axis brake|throttle|clutch] [--invert true|false] [--baseline released-value --max-correction counts] [--deadzone-low fraction] [--deadzone-high fraction] [--exponent value] [--from seconds] [--to seconds]");
    return args.Length == 0 ? 1 : 0;
}

try
{
    if (args[0] == "replay")
    {
        ReplayCommand.Execute(ReplayOptions.Parse(args[1..]));
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
        throw new ArgumentException("Expected list, monitor, snapshot, or replay. Use --help for syntax.");
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
