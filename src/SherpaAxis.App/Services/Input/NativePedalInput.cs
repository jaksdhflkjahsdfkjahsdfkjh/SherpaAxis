using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Hardware;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services.Input;

public sealed class NativePedalInput(DashboardEngine engine,
    Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> normalInput,
    Func<CancellationToken, Task<PedalControllerConnection>> connect,
    Func<HardwareRecoveryStore> createRecovery, SensorReferenceStore? references = null)
{
    private string? pendingRecovery;

    public async IAsyncEnumerable<PedalMonitorEvent> WatchAsync([EnumeratorCancellation] CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var generation = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var raw = engine.RequiresSensorInput;
            PedalControllerConnection? connection = null;
            PedalConfigurationTransaction? transaction = null;
            string? openError = null;
            if (raw)
            {
                engine.SuspendOutput("starting raw drift monitoring");
                try
                {
                    connection = await connect(token).ConfigureAwait(false);
                    if (pendingRecovery is { } path && File.Exists(path))
                    {
                        var snapshot = await HardwareRecoveryStore.ReadAsync(path, token).ConfigureAwait(false);
                        using var restore = PedalConfigurationTransaction.FromRecovery(snapshot with { Device = connection.Snapshot.Device },
                            connection.Reader, connection.Writer, new HardwareRecoveryStore(path), TimeSpan.FromSeconds(2), connection.Identity.SerialNumber);
                        await restore.RestoreSavedAsync(token).ConfigureAwait(false);
                        connection = connection with { Snapshot = connection.Snapshot with { Calibration = restore.Current } };
                        pendingRecovery = null;
                    }
                    var recovery = createRecovery();
                    var rawRanges = references is null ? null : await references.ReadAsync(connection.Identity.SerialNumber,
                        connection.Snapshot.Calibration, token).ConfigureAwait(false);
                    transaction = new(connection.Snapshot, connection.Reader, connection.Writer, recovery,
                        TimeSpan.FromSeconds(2), connection.Identity.SerialNumber);
                    pendingRecovery = recovery.FilePath;
                    await transaction.ChangeAsync(PedalHardwareSettings.Defaults, token).ConfigureAwait(false);
                    engine.SetSensorCalibration(new(connection.Identity.SerialNumber, connection.Snapshot.Calibration, rawRanges));
                }
                catch (Exception failure) when (failure is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
                { openError = failure.Message; }
                catch
                {
                    await RestoreAsync(transaction, connection, true).ConfigureAwait(false);
                    throw;
                }
            }
            if (openError is not null)
            {
                await RestoreAsync(transaction, connection, true).ConfigureAwait(false);
                yield return new(PedalConnectionState.Waiting, Stopwatch.GetElapsedTime(started), generation, Detail: openError);
                await Task.Delay(1000, token).ConfigureAwait(false);
                continue;
            }
            var disconnected = false;
            var lastConnection = -1;
            var connectedAt = Stopwatch.GetTimestamp();
            try
            {
                await foreach (var item in (connection?.Input(token) ?? normalInput(token)).WithCancellation(token).ConfigureAwait(false))
                {
                    if (engine.RequiresSensorInput != raw) { break; }
                    if (item.State == PedalConnectionState.Connected && item.ConnectionNumber != lastConnection)
                    { generation++; lastConnection = item.ConnectionNumber; connectedAt = Stopwatch.GetTimestamp(); }
                    // Windows can initially return centered or buffered reports after opening the collection.
                    if (raw && item.Axes is not null && Stopwatch.GetElapsedTime(connectedAt) < TimeSpan.FromMilliseconds(100)) { continue; }
                    yield return item with { Elapsed = Stopwatch.GetElapsedTime(started), ConnectionNumber = generation };
                    if (raw && item.State == PedalConnectionState.Disconnected) { disconnected = true; break; }
                }
            }
            finally
            {
                await RestoreAsync(transaction, connection, disconnected || token.IsCancellationRequested).ConfigureAwait(false);
            }
            if (disconnected) { await Task.Delay(1000, token).ConfigureAwait(false); }
        }
    }
    private async Task RestoreAsync(PedalConfigurationTransaction? transaction, PedalControllerConnection? connection, bool defer)
    {
        var pending = false;
        engine.SuspendOutput("input mode changed");
        try
        {
            if (transaction is not null)
            {
                try { await transaction.RestoreSavedAsync(CancellationToken.None).ConfigureAwait(false); pendingRecovery = null; }
                catch (Exception failure) when (defer && failure is IOException or UnauthorizedAccessException or TimeoutException)
                { pending = true; }
            }
        }
        finally
        {
            transaction?.Dispose();
            engine.SetSensorCalibration(null);
            if (connection is not null) { await connection.DisposeAsync().ConfigureAwait(false); }
            if (pending) { engine.SetDriftInputError("Controller restoration pending; recovery must verify the original calibration before output resumes."); }
        }
    }

}
