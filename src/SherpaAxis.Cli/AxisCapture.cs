using SherpaAxis.FeelVR;

namespace SherpaAxis.Cli;

internal static class AxisCapture
{
    public static async Task<CaptureResult> RunAsync(PedalMonitor monitor, MonitorOptions options, CancellationToken cancellationToken)
    {
        await using var file = options.CsvPath is null ? null : new FileStream(options.CsvPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous,
                BufferSize = 1
            });
        var csv = file is null ? null : new StreamWriter(file, bufferSize: 4096, leaveOpen: true);
        var display = new CaptureDisplay(Console.Out, Console.Error);
        var result = await CaptureSession.RunAsync(monitor.WatchAsync(options.DevicePath, cancellationToken), csv,
            display, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (csv is not null)
        {
            if (result.Error is not null)
            {
                // Close the unbuffered stream before disposing a failed writer, avoiding an uncancellable retry.
                await file!.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                await csv.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                result = result with { RecordedSamples = null, Error = result.Error ?? exception };
            }
        }

        display.Publish(result.Error is null
            ? $"Captured {result.AcquiredSamples} reports." + (result.RecordedSamples is { } count ? $" CSV complete: {count} rows." : "")
            : $"Capture failed: {result.Error.Message}" + (csv is null ? "" : " CSV may be incomplete."), isError: true);
        await display.CompleteAsync().ConfigureAwait(false);
        return result;
    }
}
