using System.Globalization;
using System.Threading.Channels;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Cli;

internal sealed record CaptureResult(long AcquiredSamples, long? RecordedSamples, Exception? Error);

internal static class CaptureSession
{
    public static async Task<CaptureResult> RunAsync(IAsyncEnumerable<PedalMonitorEvent> source, TextWriter? csv,
        CaptureDisplay display, int queueCapacity = 4096, TimeSpan? drainTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        var drainLimit = drainTimeout ?? TimeSpan.FromSeconds(5);
        if (drainLimit <= TimeSpan.Zero || drainLimit > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        }

        var samples = Channel.CreateBounded<PedalMonitorEvent>(new BoundedChannelOptions(queueCapacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var drain = new CancellationTokenSource();
        Exception? failure = null;
        long? recordedSamples = null;
        var statistics = new CaptureStatistics();
        var writer = csv is null ? Task.CompletedTask : Task.Run(WriteCsvAsync, CancellationToken.None);

        try
        {
            await foreach (var update in source.WithCancellation(stop.Token).ConfigureAwait(false))
            {
                var message = statistics.Observe(update);
                if (message is not null)
                {
                    display.Publish(message, isError: update.Axes is null);
                }

                if (csv is not null && update.Axes is not null && !samples.Writer.TryWrite(update))
                {
                    throw new IOException($"CSV buffer exhausted ({queueCapacity} samples). Recording stopped without silently dropping samples.");
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            samples.Writer.TryComplete();
        }

        drain.CancelAfter(drainLimit);
        await writer.ConfigureAwait(false);
        return new CaptureResult(statistics.AcquiredSamples, failure is null ? recordedSamples : null, failure);

        void Fail(Exception exception)
        {
            Interlocked.CompareExchange(ref failure, exception, null);
            stop.Cancel();
        }

        async Task WriteCsvAsync()
        {
            try
            {
                await csv!.WriteLineAsync("elapsed_seconds,connection,throttle_device_axis,brake_device_axis,clutch_device_axis".AsMemory(), drain.Token).ConfigureAwait(false);
                long written = 0;
                await foreach (var update in samples.Reader.ReadAllAsync(drain.Token).ConfigureAwait(false))
                {
                    var axes = update.Axes!.Value;
                    var line = string.Create(CultureInfo.InvariantCulture,
                        $"{update.Elapsed.TotalSeconds:F6},{update.ConnectionNumber},{axes.Throttle},{axes.Brake},{axes.Clutch}");
                    await csv.WriteLineAsync(line.AsMemory(), drain.Token).ConfigureAwait(false);
                    written++;
                }

                await csv.FlushAsync(drain.Token).ConfigureAwait(false);
                recordedSamples = written;
            }
            catch (OperationCanceledException exception) when (drain.IsCancellationRequested)
            {
                Fail(new TimeoutException("CSV did not finish within the drain deadline. Recording is incomplete.", exception));
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }
    }
}
