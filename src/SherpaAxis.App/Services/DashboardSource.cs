using System.IO;
using SherpaAxis.App.Models;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services;

public sealed class DashboardSource(DashboardEngine engine) : IAsyncDisposable
{
    private CancellationTokenSource? cancellation;
    private Task? worker;

    public bool IsRunning => worker is { IsCompleted: false };
    public string? Failure => worker?.Exception?.GetBaseException().Message;

    public async Task StartAsync(Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> source, RecordingSource? context = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        await StopAsync().ConfigureAwait(false);
        cancellation = new();
        var token = cancellation.Token;
        engine.Begin(context);
        worker = Task.Run(() => PumpAsync(source(token), token), CancellationToken.None);
    }

    public async Task StopAsync()
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
        try
        {
            await foreach (var item in source.WithCancellation(token).ConfigureAwait(false))
            {
                engine.Accept(item);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            message = "Stopped";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException)
        {
            message = $"Source error: {exception.Message}";
        }
        catch
        {
            message = "Source error: unexpected source failure.";
            throw;
        }
        finally
        {
            engine.End(message);
        }
    }
}
