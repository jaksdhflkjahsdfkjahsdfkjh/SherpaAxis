namespace SherpaAxis.FeelVR;

public sealed record HardwareCaptureState(PedalChannel Channel, int? Minimum, int? Maximum, int? Current,
    long Reports, int? Connection, bool Interrupted, bool IsFresh);

public sealed class PedalHardwareCalibration : IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly PedalConfigurationTransaction configuration;
    private readonly TimeProvider clock;
    private HardwareCaptureState? capture;
    private long? lastInputAt;
    private PedalCalibration? beforeCapture;

    public PedalHardwareCalibration(PedalConfigurationTransaction configuration, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        this.configuration = configuration;
        this.clock = clock ?? TimeProvider.System;
    }

    public void Dispose() { gate.Dispose(); GC.SuppressFinalize(this); }

    public HardwareCaptureState? Capture
    {
        get
        {
            lock (sync)
            {
                return capture is null ? null : capture with
                {
                    IsFresh = lastInputAt.HasValue && this.clock.GetElapsedTime(lastInputAt.Value) < TimeSpan.FromMilliseconds(250)
                };
            }
        }
    }

    // Start a fresh input connection after entering calibration, so queued reports from the old mode are excluded.
    public Task StartAsync(PedalChannel channel, CancellationToken cancellationToken = default) => EditAsync(async () =>
    {
        lock (sync) { if (capture is not null) { throw new InvalidOperationException("Finish or cancel the current pedal calibration first."); } }
        var previous = configuration.Current;
        await configuration.ChangeAsync(previous.WithRange(channel, PedalHardwareSettings.Defaults.Range(channel)), cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            beforeCapture = previous;
            lastInputAt = null;
            capture = new(channel, null, null, null, 0, null, false, false);
        }
    }, cancellationToken);

    public void Observe(PedalMonitorEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (sync)
        {
            if (capture is not { Interrupted: false } active) { return; }
            if ((active.Connection.HasValue && (input.State != PedalConnectionState.Connected || input.ConnectionNumber != active.Connection))
                || input.State == PedalConnectionState.Disconnected)
            {
                capture = active with { Interrupted = true };
                return;
            }
            if (input.Axes is not { } axes) { return; }
            var value = active.Channel switch { PedalChannel.Throttle => axes.Throttle, PedalChannel.Brake => axes.Brake, _ => axes.Clutch };
            if (value < 0 || value > PedalHardwareSettings.Defaults.Range(active.Channel).Maximum)
            {
                capture = active with { Interrupted = true };
                return;
            }
            capture = active with
            {
                Minimum = Math.Min(active.Minimum ?? value, value),
                Maximum = Math.Max(active.Maximum ?? value, value),
                Current = value,
                Reports = active.Reports + 1,
                Connection = input.ConnectionNumber
            };
            lastInputAt = this.clock.GetTimestamp();
        }
    }

    public Task CompleteAsync(decimal lowerPercent, decimal upperPercent, CancellationToken cancellationToken = default) => EditAsync(async () =>
    {
        var current = Capture ?? throw new InvalidOperationException("Start hardware calibration first.");
        if (current.Interrupted) { throw new InvalidOperationException("Input connection changed. Cancel and restore before calibrating again."); }
        if (!current.IsFresh || !current.Minimum.HasValue || !current.Maximum.HasValue)
        {
            throw new InvalidOperationException("Wait for fresh pedal readings before clicking Done.");
        }
        var range = PedalHardwareSettings.ApplyDeadzones(new(current.Minimum.Value, current.Maximum.Value), lowerPercent, upperPercent);
        await configuration.ChangeAsync(configuration.Current.WithRange(current.Channel, range), cancellationToken).ConfigureAwait(false);
        lock (sync) { capture = null; beforeCapture = null; lastInputAt = null; }
    }, cancellationToken);

    public Task CancelAsync(CancellationToken cancellationToken = default) => EditAsync(async () =>
    {
        PedalCalibration? previous;
        lock (sync) { previous = beforeCapture; }
        if (previous is not null) { await configuration.ChangeAsync(previous, cancellationToken).ConfigureAwait(false); }
        lock (sync) { capture = null; beforeCapture = null; lastInputAt = null; }
    }, cancellationToken);

    public Task RestoreSavedAsync(CancellationToken cancellationToken = default) => EditAsync(async () =>
    {
        await configuration.RestoreSavedAsync(cancellationToken).ConfigureAwait(false);
        lock (sync) { capture = null; beforeCapture = null; lastInputAt = null; }
    }, cancellationToken);

    private async Task EditAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
