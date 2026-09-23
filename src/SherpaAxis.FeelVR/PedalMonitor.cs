using System.Diagnostics;
using System.Runtime.CompilerServices;
using SherpaAxis.Devices;

namespace SherpaAxis.FeelVR;

public sealed class PedalMonitor
{
    private readonly IInputReportSource source;
    private readonly TimeSpan retryInterval;
    private readonly TimeSpan readTimeout;

    public PedalMonitor(IInputReportSource source, TimeSpan retryInterval, TimeSpan readTimeout)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (retryInterval <= TimeSpan.Zero || retryInterval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(retryInterval));
        }

        if (readTimeout <= TimeSpan.Zero || readTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(readTimeout));
        }

        this.source = source;
        this.retryInterval = retryInterval;
        this.readTimeout = readTimeout;
    }

    public async IAsyncEnumerable<PedalMonitorEvent> WatchAsync(
        string? devicePath = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var connectionNumber = 0;
        string? lastWaitingDetail = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IInputReportConnection? connection = null;
            string? failure = null;
            try
            {
                var candidates = source.Find(PedalInputReport.VendorId, PedalInputReport.ProductId)
                    .Where(PedalInputReport.Supports)
                    .Where(device => devicePath is null || string.Equals(device.Path, devicePath, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (candidates.Length > 1)
                {
                    throw new InvalidOperationException("Multiple pedals match. Select one using --device and its path from list.");
                }

                if (candidates.Length == 1)
                {
                    connection = source.Open(candidates[0]);
                }
                else
                {
                    failure = "No compatible input collection is visible.";
                }
            }
            catch (IOException exception)
            {
                failure = exception.Message;
            }
            catch (UnauthorizedAccessException exception)
            {
                failure = exception.Message;
            }

            if (connection is null)
            {
                if (failure != lastWaitingDetail)
                {
                    yield return new(PedalConnectionState.Waiting, Stopwatch.GetElapsedTime(started), connectionNumber, Detail: failure);
                    lastWaitingDetail = failure;
                }

                await Task.Delay(retryInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            lastWaitingDetail = null;
            await using (connection.ConfigureAwait(false))
            {
                connectionNumber++;
                yield return new(PedalConnectionState.Connected, Stopwatch.GetElapsedTime(started), connectionNumber);
                var buffer = new byte[PedalInputReport.Length];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PedalAxes axes;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(readTimeout);
                    try
                    {
                        var length = await connection.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                        axes = PedalInputReport.Decode(buffer.AsSpan(0, length));
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        failure = "Input report timeout.";
                        break;
                    }
                    catch (IOException exception)
                    {
                        failure = exception.Message;
                        break;
                    }
                    catch (InvalidDataException exception)
                    {
                        failure = exception.Message;
                        break;
                    }

                    yield return new(PedalConnectionState.Connected, Stopwatch.GetElapsedTime(started), connectionNumber, axes);
                }
            }

            yield return new(PedalConnectionState.Disconnected, Stopwatch.GetElapsedTime(started), connectionNumber, Detail: failure);
            await Task.Delay(retryInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
