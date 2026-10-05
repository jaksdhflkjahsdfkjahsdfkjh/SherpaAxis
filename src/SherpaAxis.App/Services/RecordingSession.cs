using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using SherpaAxis.App.Models;
using SherpaAxis.Core;

namespace SherpaAxis.App.Services;

public sealed record RecordingStatus(long AcceptedSamples, long WrittenSamples, bool IsStopping, bool IsComplete, Exception? Failure);

public sealed class RecordingSession : IDashboardRecording, IAsyncDisposable
{
    private readonly object sync = new();
    private readonly TextWriter input;
    private readonly TextWriter processed;
    private readonly TextWriter events;
    private readonly RecordingFiles? files;
    private readonly Channel<Entry> queue;
    private readonly CancellationTokenSource drain = new();
    private readonly TimeSpan drainTimeout;
    private readonly int capacity;
    private readonly Task writer;
    private long sequence;
    private long accepted;
    private long written;
    private Exception? failure;
    private bool stopping;
    private bool finished;
    private string stopReason = "unfinished";

    // Supplied writers are borrowed; file-backed recordings use CreateAsync and own their streams.
    public RecordingSession(TextWriter input, TextWriter processed, TextWriter events, int queueCapacity = 4096, TimeSpan? drainTimeout = null)
        : this(input, processed, events, queueCapacity, drainTimeout, null) { }

    private RecordingSession(TextWriter input, TextWriter processed, TextWriter events, int queueCapacity, TimeSpan? drainTimeout, RecordingFiles? files)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(processed);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        this.drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(5);
        if (this.drainTimeout <= TimeSpan.Zero || this.drainTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        }

        this.input = input;
        this.processed = processed;
        this.events = events;
        this.files = files;
        capacity = queueCapacity;
        queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        writer = Task.Run(WriteAsync, CancellationToken.None);
    }

    public RecordingStatus Status
    {
        get { lock (sync) { return new(accepted, Interlocked.Read(ref written), stopping, finished, failure); } }
    }

    public static Task<RecordingSession> CreateAsync(string inputPath, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var files = RecordingFiles.Create(inputPath);
        return new RecordingSession(files.Input, files.Processed, files.Events, 4096, null, files);
    }, cancellationToken);

    public void RecordSample(RecordedSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Enqueue(sample, null);
    }

    public void RecordEvent(RecordedEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Enqueue(null, item);
    }

    public void Complete(string reason)
    {
        lock (sync)
        {
            if (stopping || finished)
            {
                return;
            }

            stopReason = reason;
            if (reason == "source-error")
            {
                Fail(new IOException("The source ended with an error. Recording is incomplete."));
                return;
            }

            stopping = true;
            queue.Writer.TryComplete();
            drain.CancelAfter(drainTimeout);
        }
    }

    public async Task<RecordingStatus> StopAsync()
    {
        Complete("user-finished");
        await writer.ConfigureAwait(false);
        return Status;
    }

    public void Abort(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Fail(exception);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Enqueue(RecordedSample? sample, RecordedEvent? item)
    {
        lock (sync)
        {
            if (stopping || finished)
            {
                return;
            }

            if (!queue.Writer.TryWrite(new(++sequence, DateTimeOffset.UtcNow, sample, item)))
            {
                Fail(new IOException($"Recording buffer exhausted ({capacity} entries). Recording is incomplete; no reports were silently dropped."));
            }
            else if (sample is not null)
            {
                accepted++;
            }
        }
    }

    private void Fail(Exception exception)
    {
        lock (sync)
        {
            failure ??= exception;
            if (!finished)
            {
                stopping = true;
                queue.Writer.TryComplete();
                drain.CancelAfter(TimeSpan.Zero);
            }
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            var token = drain.Token;
            await input.WriteLineAsync(RecordingFormat.InputHeader.AsMemory(), token).ConfigureAwait(false);
            await processed.WriteLineAsync(RecordingFormat.ProcessedHeader.AsMemory(), token).ConfigureAwait(false);
            var previousRelease = (Throttle: false, Brake: false, Clutch: false);
            await foreach (var entry in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (entry.Sample is { } sample)
                {
                    await input.WriteLineAsync(RecordingFormat.Input(sample).AsMemory(), token).ConfigureAwait(false);
                    await processed.WriteLineAsync(RecordingFormat.Processed(entry.Sequence, sample).AsMemory(), token).ConfigureAwait(false);
                    if (sample.Throttle.ResetReason != AxisSessionResetReason.None || sample.Brake.ResetReason != AxisSessionResetReason.None || sample.Clutch.ResetReason != AxisSessionResetReason.None)
                    {
                        await EventAsync(entry, new("session-reset", sample.ElapsedSeconds, sample.ConfigurationRevision,
                            new { sample.Connection, Throttle = sample.Throttle.ResetReason, Brake = sample.Brake.ResetReason, Clutch = sample.Clutch.ResetReason })).ConfigureAwait(false);
                    }

                    var release = (sample.Throttle.ReleaseConfirmed, sample.Brake.ReleaseConfirmed, sample.Clutch.ReleaseConfirmed);
                    if (release != previousRelease)
                    {
                        await EventAsync(entry, new("release-permission-changed", sample.ElapsedSeconds, sample.ConfigurationRevision,
                            new { Throttle = release.Item1, Brake = release.Item2, Clutch = release.Item3 })).ConfigureAwait(false);
                        previousRelease = release;
                    }

                    Interlocked.Increment(ref written);
                }
                else if (entry.Event is { } item)
                {
                    await EventAsync(entry, item).ConfigureAwait(false);
                    await events.FlushAsync(token).ConfigureAwait(false);
                }
            }

            await input.FlushAsync(token).ConfigureAwait(false);
            await processed.FlushAsync(token).ConfigureAwait(false);
            lock (sync)
            {
                if (failure is not null)
                {
                    return;
                }
            }

            await events.WriteLineAsync(JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Kind = "recording-completed",
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Complete = true,
                AcceptedSamples = accepted,
                WrittenSamples = written,
                Reason = stopReason
            }, RecordingFormat.JsonOptions).AsMemory(), token).ConfigureAwait(false);
            await events.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            Fail(new TimeoutException("Recording did not drain within its deadline. Files are incomplete.", exception));
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            if (files is not null && await files.CloseAsync().ConfigureAwait(false) is { } closeError)
            {
                Fail(closeError);
            }

            lock (sync)
            {
                finished = true;
                stopping = true;
                drain.Dispose();
            }
        }
    }

    private Task EventAsync(Entry entry, RecordedEvent item) => events.WriteLineAsync(JsonSerializer.Serialize(new
    {
        SchemaVersion = 1,
        entry.Sequence,
        EnqueuedAtUtc = entry.Utc,
        item.Kind,
        item.ElapsedSeconds,
        item.ConfigurationRevision,
        item.Details
    }, RecordingFormat.JsonOptions).AsMemory(), drain.Token);

    private sealed record Entry(long Sequence, DateTimeOffset Utc, RecordedSample? Sample, RecordedEvent? Event);
}
