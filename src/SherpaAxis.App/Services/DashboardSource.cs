using System.IO;
using SherpaAxis.App.Models;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services;

public sealed class DashboardSource(DashboardEngine engine) : IAsyncDisposable
{
    public DiagnosticLog? Log { get; set; }
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private readonly object stopSync = new();
    private Task? stopping;

    public bool IsRunning => worker is { IsCompleted: false };
    public string? Failure => worker?.Exception?.GetBaseException().Message;

    public async Task StartAsync(Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> source, RecordingSource? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await StopAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cancellation.Token;
        engine.Begin(context);
        worker = Task.Run(() => PumpAsync(source(token), token), CancellationToken.None);
    }

    public Task StopAsync()
    {
        lock (stopSync) { return stopping is { IsCompleted: false } ? stopping : stopping = StopCoreAsync(); }
    }
    private async Task StopCoreAsync()
    {
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            if (worker is not null)
            {
                await worker.ConfigureAwait(false);
            }
        }
        finally
        {
            cancellation?.Dispose();
            cancellation = null;
            worker = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task PumpAsync(IAsyncEnumerable<PedalMonitorEvent> source, CancellationToken token)
    {
        var message = "Playback complete";
        PedalConnectionState? previousState = null;
        var previousConnection = -1;
        try
        {
            await foreach (var item in source.WithCancellation(token).ConfigureAwait(false))
            {
                if (item.State != previousState || item.ConnectionNumber != previousConnection)
                {
                    previousState = item.State; previousConnection = item.ConnectionNumber;
                    Log?.Write("input-connection", $"{item.State} · connection {item.ConnectionNumber}");
                }
                engine.Accept(item);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            message = "Stopped";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException)
        {
            Log?.Write("input", "Pedal input failed.", exception);
            message = $"Source error: {exception.Message}";
        }
        catch (Exception exception)
        {
            Log?.Write("input", "Unexpected pedal input failure.", exception);
            message = "Source error: unexpected source failure.";
            throw;
        }
        finally
        {
            engine.End(message);
        }
    }
}
