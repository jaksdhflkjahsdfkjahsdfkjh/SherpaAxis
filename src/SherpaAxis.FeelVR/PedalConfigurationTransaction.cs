using SherpaAxis.Devices;

namespace SherpaAxis.FeelVR;

public sealed class PedalConfigurationTransaction : IDisposable
{
    private readonly IFeatureReportReader reader;
    private readonly IFeatureReportWriter writer;
    private readonly IHardwareRecoveryStore recovery;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan timeout;
    private readonly DateTimeOffset createdAt = DateTimeOffset.UtcNow;
    private PedalCalibration? attempted;
    private PedalCalibration? uncertainSaved;
    private readonly string? serialNumber;

    public PedalConfigurationTransaction(CalibrationSnapshot snapshot, IFeatureReportReader reader,
        IFeatureReportWriter writer, IHardwareRecoveryStore recovery, TimeSpan timeout, string? serialNumber = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(recovery);
        if (!PedalCalibrationReport.Supports(snapshot.Device)) { throw new ArgumentException("Select a compatible pedal configuration collection.", nameof(snapshot)); }
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) { throw new ArgumentOutOfRangeException(nameof(timeout)); }
        PedalHardwareSettings.Validate(snapshot.Calibration);
        Device = snapshot.Device;
        Current = Saved = snapshot.Calibration;
        this.reader = reader;
        this.writer = writer;
        this.recovery = recovery;
        this.timeout = timeout;
        this.serialNumber = serialNumber;
    }

    public HidDeviceInfo Device { get; }
    public PedalCalibration Current { get; private set; }
    public PedalCalibration Saved { get; private set; }
    public bool IsDirty => Current != Saved;
    public bool NeedsRecovery { get; private set; }
    public bool PersistenceUncertain { get; private set; }

    public async Task<HardwareRecoverySnapshot> CreateHardwareBackupAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (NeedsRecovery || PersistenceUncertain || string.IsNullOrWhiteSpace(serialNumber))
            { throw new InvalidOperationException("Resolve recovery and verify the controller identity before backing up hardware."); }
            await EnsureExpectedAsync(cancellationToken).ConfigureAwait(false);
            return new(2, DateTimeOffset.UtcNow, Device, Current, Current, null, SerialNumber: serialNumber, IsManualBackup: true);
        }
        finally { gate.Release(); }
    }

    public Task RestoreHardwareBackupAsync(HardwareRecoverySnapshot backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (backup.SchemaVersion != 2 || !backup.IsManualBackup || backup.SaveUncertain || backup.Attempted is not null || backup.Saved != backup.LastConfirmed)
        { throw new ArgumentException("Select a valid manual hardware backup.", nameof(backup)); }
        if (backup.Device != Device || string.IsNullOrWhiteSpace(backup.SerialNumber) || string.IsNullOrWhiteSpace(serialNumber)
            || !PedalDeviceIdentity.SameController(backup.SerialNumber, serialNumber))
        { throw new IOException("Hardware backup belongs to a different physical controller."); }
        return ChangeAsync(backup.Saved, cancellationToken);
    }

    public void Dispose() { gate.Dispose(); GC.SuppressFinalize(this); }

    public static PedalConfigurationTransaction FromRecovery(HardwareRecoverySnapshot snapshot, IFeatureReportReader reader,
        IFeatureReportWriter writer, IHardwareRecoveryStore recovery, TimeSpan timeout, string? actualSerialNumber = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != 1) { throw new ArgumentException("Unsupported hardware recovery version.", nameof(snapshot)); }
        if (snapshot.SerialNumber is not null && (actualSerialNumber is null || !PedalDeviceIdentity.SameController(snapshot.SerialNumber, actualSerialNumber)))
        {
            throw new IOException("Recovery backup belongs to a different physical controller serial number.");
        }
        PedalHardwareSettings.Validate(snapshot.LastConfirmed);
        if (snapshot.Attempted is not null) { PedalHardwareSettings.Validate(snapshot.Attempted); }
        return new(new(1, snapshot.CreatedAtUtc, snapshot.Device, snapshot.Saved), reader, writer, recovery, timeout, snapshot.SerialNumber)
        {
            Current = snapshot.LastConfirmed,
            attempted = snapshot.Attempted,
            NeedsRecovery = true,
            PersistenceUncertain = snapshot.SaveUncertain,
            uncertainSaved = snapshot.SaveUncertain ? snapshot.Attempted ?? snapshot.LastConfirmed : null
        };
    }

    public async Task ChangeAsync(PedalCalibration calibration, CancellationToken cancellationToken = default)
    {
        PedalHardwareSettings.Validate(calibration);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (NeedsRecovery || PersistenceUncertain) { throw new InvalidOperationException("Restore the hardware backup and resolve uncertain Save completion before making more changes."); }
            await EnsureExpectedAsync(cancellationToken).ConfigureAwait(false);
            if (calibration == Current) { return; }
            attempted = calibration;
            try { await StoreAsync(false, cancellationToken).ConfigureAwait(false); }
            catch { attempted = null; throw; }
            var previous = Current;
            try
            {
                await WriteVerifiedAsync(calibration, cancellationToken).ConfigureAwait(false);
                Current = calibration;
                attempted = null;
                await StoreOrClearAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (IsTransferFailure(failure))
            {
                try
                {
                    // A timeout/cancelled SET_REPORT may already have changed the device.
                    await RestoreKnownAsync(previous, CancellationToken.None).ConfigureAwait(false);
                    Current = previous;
                    attempted = null;
                    await StoreOrClearAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreFailure) when (IsTransferFailure(restoreFailure))
                {
                    NeedsRecovery = true;
                    throw new IOException("Hardware change failed and restoration could not be verified. Keep the recovery backup and reconnect the same controller.", new AggregateException(failure, restoreFailure));
                }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (NeedsRecovery) { throw new InvalidOperationException("Resolve hardware recovery before saving."); }
            await EnsureExpectedAsync(cancellationToken).ConfigureAwait(false);
            await StoreAsync(true, cancellationToken).ConfigureAwait(false);
            try
            {
                await TransferAsync(token => writer.WriteAsync(Device, PedalCommandReport.Encode(PedalDeviceCommand.SaveConfiguration), token), cancellationToken).ConfigureAwait(false);
                await EnsureExpectedAsync(cancellationToken).ConfigureAwait(false);
                await recovery.ClearAsync(cancellationToken).ConfigureAwait(false);
                Saved = Current;
                attempted = uncertainSaved = null;
                PersistenceUncertain = false;
            }
            catch (Exception failure) when (IsTransferFailure(failure))
            {
                PersistenceUncertain = true;
                uncertainSaved = Current;
                NeedsRecovery = true;
                throw new IOException("Save completion is uncertain. The recovery backup is retained; verify calibration after reconnecting before retrying Save.", failure);
            }
        }
        finally { gate.Release(); }
    }

    public async Task RestoreSavedAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsDirty && attempted is null && !NeedsRecovery && !PersistenceUncertain) { return; }
            await RestoreKnownAsync(Saved, cancellationToken).ConfigureAwait(false);
            Current = Saved;
            attempted = uncertainSaved;
            if (PersistenceUncertain) { await StoreAsync(true, cancellationToken).ConfigureAwait(false); }
            else { await recovery.ClearAsync(cancellationToken).ConfigureAwait(false); }
            NeedsRecovery = false;
        }
        catch (Exception failure) when (IsTransferFailure(failure))
        {
            NeedsRecovery = true;
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task RestoreKnownAsync(PedalCalibration target, CancellationToken cancellationToken)
    {
        var actual = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (actual == target) { return; }
        if (actual != Current && actual != attempted)
        {
            throw new IOException("Hardware configuration changed outside this session. Refusing to overwrite unknown settings; keep the backup for review.");
        }
        await WriteVerifiedAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureExpectedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await ReadAsync(cancellationToken).ConfigureAwait(false) != Current)
            {
                throw new IOException("Hardware configuration changed outside this session. Close FEEL-VR Control and read the controller again.");
            }
        }
        catch (Exception failure) when (IsTransferFailure(failure)) { if (IsDirty || attempted is not null) { NeedsRecovery = true; } throw; }
    }

    private async Task WriteVerifiedAsync(PedalCalibration calibration, CancellationToken cancellationToken)
    {
        await TransferAsync(token => writer.WriteAsync(Device, PedalCalibrationReport.Encode(calibration), token), cancellationToken).ConfigureAwait(false);
        if (await ReadAsync(cancellationToken).ConfigureAwait(false) != calibration) { throw new IOException("Controller calibration readback did not match the requested settings."); }
    }

    private async Task<PedalCalibration> ReadAsync(CancellationToken cancellationToken)
    {
        byte[]? report = null;
        await TransferAsync(async token => { report = await reader.ReadAsync(Device, PedalCalibrationReport.ReportId, token).ConfigureAwait(false); }, cancellationToken).ConfigureAwait(false);
        return PedalCalibrationReport.Decode(report!);
    }

    private async Task TransferAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try { await operation(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Pedal configuration transfer timed out.", failure);
        }
    }

    private Task StoreAsync(bool uncertain, CancellationToken cancellationToken) => recovery.WriteAsync(
        new(1, createdAt, Device, Saved, Current, attempted, uncertain || PersistenceUncertain, serialNumber), cancellationToken);

    private Task StoreOrClearAsync(CancellationToken cancellationToken) => IsDirty || PersistenceUncertain
        ? StoreAsync(false, cancellationToken) : recovery.ClearAsync(cancellationToken);

    private static bool IsTransferFailure(Exception failure) => failure is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException;
}
