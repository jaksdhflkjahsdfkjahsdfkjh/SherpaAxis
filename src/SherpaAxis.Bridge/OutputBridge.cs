namespace SherpaAxis.Bridge;

public sealed class OutputBridge : IProcessedOutputSink, IAsyncDisposable
{
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PedalOutput latest;
    private long? receivedAt;
    private bool invalidated;
    private bool disposed;
    private string status = "Connecting…";

    public OutputBridge(Func<IVirtualController> connect, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(connect);
        this.clock = clock ?? TimeProvider.System;
        worker = Task.Run(() => RunAsync(connect));
    }

    public Task Ready => ready.Task;
    public string Status { get { lock (sync) { return status; } } }
    public void Submit(PedalOutput output)
    {
        lock (sync)
        {
            if (!invalidated) { latest = output; receivedAt = clock.GetTimestamp(); }
        }
    }
    public void Invalidate(string reason)
    {
        lock (sync) { invalidated = true; status = $"Output stopped: {reason}. Enable again to resume."; }
    }

    private async Task RunAsync(Func<IVirtualController> connect)
    {
        IVirtualController? controller = null;
        try
        {
            controller = connect();
            controller.Write(default);
            lock (sync) { if (!invalidated) { status = "Output enabled · throttle X · brake Y · clutch Z"; } }
            ready.TrySetResult();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4));
            var started = clock.GetTimestamp();
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                PedalOutput next;
                lock (sync)
                {
                    if (invalidated) { break; }
                    if (clock.GetElapsedTime(receivedAt ?? started) >= TimeSpan.FromMilliseconds(100))
                    {
                        invalidated = true;
                        status = "Output stopped: no fresh input for 100 ms. Enable again to resume.";
                        break;
                    }
                    next = receivedAt.HasValue ? latest : default;
                }
                controller.Write(next);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { ready.TrySetCanceled(); }
        catch (Exception exception)
        {
            lock (sync) { invalidated = true; status = $"Output failed: {exception.Message}"; }
            ready.TrySetException(exception);
        }
        finally
        {
            if (controller is not null)
            {
                try { controller.Write(default); }
                catch (Exception exception) { lock (sync) { status = $"Output failed while releasing axes: {exception.Message}"; } }
                try { controller.Dispose(); }
                catch (Exception exception) { lock (sync) { status = $"Output failed while releasing device: {exception.Message}"; } }
            }
            lock (sync) { invalidated = true; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        disposed = true;
        Invalidate("disabled");
        await stop.CancelAsync().ConfigureAwait(false);
        await worker.ConfigureAwait(false);
        stop.Dispose();
        GC.SuppressFinalize(this);
    }
}
