using System.IO;
using SherpaAxis.App.Models;
using SherpaAxis.Bridge;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services;

public sealed record PedalRuntimeServices(Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> Input, Func<IVirtualController> Output)
{
    public static PedalRuntimeServices Default(AutomaticVJoy? vjoy = null) => new(token =>
        new PedalMonitor(new WindowsHidInputSource(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).WatchAsync(cancellationToken: token),
        (vjoy ?? AutomaticVJoy.Default()).Connect);
}

public sealed class PedalRuntime(DashboardEngine engine, DashboardSource source, PedalRuntimeServices services, IButtonStateSource buttons, DiagnosticLog? log = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    private OutputBridge? output;
    private bool paused = true, disposed;
    private long retryAfter;
    private string status = "Waiting for pedal input";
    public string Status => Volatile.Read(ref status);
    private void SetStatus(string value)
    {
        if (Interlocked.Exchange(ref status, value) != value) { log?.Write("output", value); }
    }
    public bool IsPaused => Volatile.Read(ref paused);
    public void StopPublishing() { Volatile.Write(ref paused, true); engine.End("App closing"); }

    public async Task ResumeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed || !paused) { return; }
            await source.StartAsync(services.Input, new("live-device-axis"), stop.Token);
            paused = false;
            worker ??= Task.Run(RunAsync);
        }
        finally { gate.Release(); }
    }
    public async Task PauseAsync()
    {
        await gate.WaitAsync();
        try
        {
            paused = true;
            engine.SuspendOutput("hardware calibration");
            await source.StopAsync();
            if (output is not null) { await output.DisposeAsync(); output = null; }
            SetStatus("Output paused · hardware calibration");
        }
        finally { gate.Release(); }
    }
    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                await gate.WaitAsync(stop.Token);
                try
                {
                    if (paused) { continue; }
                    engine.ProcessCorrectionButtons(buttons);
                    if (output is { IsActive: true }) { SetStatus(output.Status); continue; }
                    if (output is not null)
                    {
                        var previousStatus = output.Status;
                        SetStatus(previousStatus);
                        if (previousStatus.StartsWith("Output failed", StringComparison.Ordinal)) { retryAfter = Environment.TickCount64 + 1000; }
                        await output.DisposeAsync(); output = null;
                    }
                    if (!engine.CanPublishOutput) { SetStatus("vJoy ready when fresh pedal input returns"); continue; }
                    if (Environment.TickCount64 < retryAfter) { continue; }
                    var next = new OutputBridge(services.Output);
                    try
                    {
                        await next.Ready;
                        stop.Token.ThrowIfCancellationRequested();
                        if (paused) { throw new OperationCanceledException("Output is paused."); }
                        engine.AttachOutput(next);
                        output = next;
                        retryAfter = 0;
                        SetStatus(next.Status);
                    }
                    catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                        or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or OperationCanceledException)
                    {
                        retryAfter = Environment.TickCount64 + 1000;
                        await next.DisposeAsync();
                        log?.Write("output-error", "Could not start virtual output.", failure);
                        SetStatus($"vJoy unavailable · {failure.Message}");
                    }
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        disposed = true;
        engine.SuspendOutput("app closing");
        Exception? failure = null;
        try { await stop.CancelAsync(); }
        catch (Exception error) { failure = error; }
        try { if (worker is not null) { await worker; } }
        catch (Exception error) { failure ??= error; }
        try { await source.StopAsync(); }
        catch (Exception error) { failure ??= error; }
        try { if (output is not null) { await output.DisposeAsync(); output = null; } }
        catch (Exception error) { failure ??= error; }
        finally { stop.Dispose(); gate.Dispose(); GC.SuppressFinalize(this); }
        if (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
    }
}
