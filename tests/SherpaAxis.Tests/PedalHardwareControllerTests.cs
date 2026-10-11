using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Tests;

public sealed class PedalHardwareControllerTests
{
    private static readonly PedalCalibration Original = new(new(100, 1000), new(200, 2000), new(300, 3000));

    [Theory]
    [InlineData(PedalChannel.Throttle)]
    [InlineData(PedalChannel.Brake)]
    [InlineData(PedalChannel.Clutch)]
    public async Task HardwareCalibrationCapturesTheIdentityModeThenAppliesDeadzoneAndSeparateSave(PedalChannel channel)
    {
        var device = new Device();
        var backup = new Backup();
        using var transaction = Create(device, backup);
        using var calibration = new PedalHardwareCalibration(transaction);
        await calibration.StartAsync(channel);
        Assert.Equal(PedalHardwareSettings.Defaults.Range(channel), device.State.Range(channel));
        Assert.All(Enum.GetValues<PedalChannel>().Where(c => c != channel), c => Assert.Equal(Original.Range(c), device.State.Range(c)));
        calibration.Observe(Sample(channel, 100));
        calibration.Observe(Sample(channel, 900));
        calibration.Observe(Sample(channel, 300));
        Assert.Equal(3, calibration.Capture!.Reports);
        await calibration.CompleteAsync(5, 5);
        Assert.Equal(new(140, 860), device.State.Range(channel));
        Assert.Null(calibration.Capture);
        Assert.True(transaction.IsDirty);
        Assert.NotNull(backup.Snapshot);
        Assert.DoesNotContain(device.Writes, bytes => bytes[0] == 2);
        await transaction.SaveAsync();
        Assert.Equal(2, device.Writes[^1][0]);
        Assert.Equal(3, device.Writes[^1][1]);
        Assert.False(transaction.IsDirty);
        Assert.Null(backup.Snapshot);
        Assert.Equal(device.State, transaction.Saved);
    }

    [Fact]
    public async Task CancellingOnePedalKeepsEarlierUnsavedChangesAndGlobalCancelRestoresSaved()
    {
        var device = new Device();
        var backup = new Backup();
        using var transaction = Create(device, backup);
        var edited = Original.WithRange(PedalChannel.Throttle, new(110, 900));
        await transaction.ChangeAsync(edited);
        using var calibration = new PedalHardwareCalibration(transaction);
        await calibration.StartAsync(PedalChannel.Brake);
        await calibration.CancelAsync();
        Assert.Equal(edited, device.State);
        Assert.True(transaction.IsDirty);
        await calibration.RestoreSavedAsync();
        Assert.Equal(Original, device.State);
        Assert.False(transaction.IsDirty);
        Assert.Null(backup.Snapshot);
    }

    [Fact]
    public async Task FailedWriteThatChangedHardwareIsReadBackAndRolledBackWithoutSaving()
    {
        var device = new Device { FailWriteAfterApply = true };
        var backup = new Backup();
        using var transaction = Create(device, backup);
        await Assert.ThrowsAsync<IOException>(() => transaction.ChangeAsync(PedalHardwareSettings.Defaults));
        Assert.Equal(Original, device.State);
        Assert.Equal(Original, transaction.Current);
        Assert.False(transaction.NeedsRecovery);
        Assert.Null(backup.Snapshot);
        Assert.Equal(2, device.Writes.Count);
        Assert.All(device.Writes, bytes => Assert.Equal(3, bytes[0]));
    }

    [Fact]
    public async Task RemovalDuringWriteKeepsRecoveryUntilTheSameDeviceCanBeRestored()
    {
        var device = new Device { DisconnectAfterWrite = true };
        var backup = new Backup();
        using var transaction = Create(device, backup);
        await Assert.ThrowsAsync<IOException>(() => transaction.ChangeAsync(PedalHardwareSettings.Defaults));
        Assert.True(transaction.NeedsRecovery);
        Assert.Equal(PedalHardwareSettings.Defaults, backup.Snapshot!.Attempted);
        Assert.Equal(Original, backup.Snapshot.Saved);
        device.Disconnected = false;
        device.DisconnectAfterWrite = false;
        using var recovered = PedalConfigurationTransaction.FromRecovery(backup.Snapshot, device, device, backup, TimeSpan.FromSeconds(1));
        await recovered.RestoreSavedAsync();
        Assert.Equal(Original, device.State);
        Assert.False(recovered.NeedsRecovery);
        Assert.Null(backup.Snapshot);
    }

    [Fact]
    public async Task BackupFailureAndUnrelatedExternalChangesCannotBeOverwritten()
    {
        var device = new Device();
        var backup = new Backup { FailWrites = true };
        using var transaction = Create(device, backup);
        await Assert.ThrowsAsync<IOException>(() => transaction.ChangeAsync(PedalHardwareSettings.Defaults));
        Assert.Empty(device.Writes);
        backup.FailWrites = false;
        device.State = Original.WithRange(PedalChannel.Brake, new(300, 1800));
        await Assert.ThrowsAsync<IOException>(() => transaction.ChangeAsync(PedalHardwareSettings.Defaults));
        Assert.Empty(device.Writes);
        Assert.Equal(new(300, 1800), device.State.Brake);
    }

    [Fact]
    public async Task BackupFailureAfterAConfirmedHardwareWriteRestoresThePreviousPendingSettings()
    {
        var device = new Device();
        var backup = new Backup();
        using var transaction = Create(device, backup);
        var pending = Original.WithRange(PedalChannel.Throttle, new(110, 900));
        await transaction.ChangeAsync(pending);
        backup.FailOnWrite = backup.WriteCount + 2;
        await Assert.ThrowsAsync<IOException>(() => transaction.ChangeAsync(PedalHardwareSettings.Defaults));
        Assert.Equal(pending, device.State);
        Assert.Equal(pending, transaction.Current);
        Assert.True(transaction.IsDirty);
        Assert.False(transaction.NeedsRecovery);
        Assert.Equal(pending, backup.Snapshot!.LastConfirmed);
        Assert.Equal(Original, backup.Snapshot.Saved);
        await transaction.RestoreSavedAsync();
        Assert.Equal(Original, device.State);
        Assert.Null(backup.Snapshot);
        Assert.All(device.Writes, report => Assert.Equal(3, report[0]));
    }

    [Fact]
    public async Task SaveFailureKeepsPersistentUncertaintyEvenAfterVolatileRestore()
    {
        var device = new Device();
        var backup = new Backup();
        using var transaction = Create(device, backup);
        await transaction.ChangeAsync(PedalHardwareSettings.Defaults);
        device.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => transaction.SaveAsync());
        Assert.True(transaction.NeedsRecovery);
        Assert.True(backup.Snapshot!.SaveUncertain);
        await transaction.RestoreSavedAsync();
        Assert.Equal(Original, device.State);
        Assert.True(transaction.PersistenceUncertain);
        Assert.True(backup.Snapshot!.SaveUncertain);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ChangeAsync(PedalHardwareSettings.Defaults));
        device.FailSave = false;
        await transaction.SaveAsync();
        Assert.False(transaction.PersistenceUncertain);
        Assert.Null(backup.Snapshot);
    }

    [Fact]
    public void RecoveryRequiresTheSamePhysicalSerialEvenAtTheSameCollectionPath()
    {
        var device = new Device();
        var backup = new Backup();
        var snapshot = new HardwareRecoverySnapshot(1, DateTimeOffset.UtcNow, PedalCalibrationReportTests.Collection,
            Original, PedalHardwareSettings.Defaults, null, SerialNumber: "PE001700");
        Assert.Throws<IOException>(() => PedalConfigurationTransaction.FromRecovery(snapshot, device, device, backup, TimeSpan.FromSeconds(1), "PE0017FF"));
        using var accepted = PedalConfigurationTransaction.FromRecovery(snapshot, device, device, backup, TimeSpan.FromSeconds(1), "PE001800");
        Assert.True(accepted.NeedsRecovery);
        Assert.Empty(device.Writes);
    }

    [Fact]
    public async Task MissingMotionStaleSamplesAndReconnectCannotFinishACapture()
    {
        var device = new Device();
        var backup = new Backup();
        var clock = new ManualClock();
        using var transaction = Create(device, backup);
        using var calibration = new PedalHardwareCalibration(transaction, clock);
        await calibration.StartAsync(PedalChannel.Brake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => calibration.CompleteAsync(0, 0));
        calibration.Observe(Sample(PedalChannel.Brake, 100));
        await Assert.ThrowsAsync<ArgumentException>(() => calibration.CompleteAsync(0, 0));
        calibration.Observe(Sample(PedalChannel.Brake, 900));
        clock.Advance(251);
        await Assert.ThrowsAsync<InvalidOperationException>(() => calibration.CompleteAsync(0, 0));
        calibration.Observe(Sample(PedalChannel.Brake, 800) with { ConnectionNumber = 2 });
        Assert.True(calibration.Capture!.Interrupted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => calibration.CompleteAsync(0, 0));
        await calibration.RestoreSavedAsync();
        Assert.Equal(Original, device.State);
    }

    private static PedalConfigurationTransaction Create(Device device, Backup backup) =>
        new(new(1, DateTimeOffset.UtcNow, PedalCalibrationReportTests.Collection, Original), device, device, backup, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task ManualBackupReadsWithoutWritesAndCanRestoreAfterDifferentSettingsWereSaved()
    {
        var device = new Device();
        var recovery = new Backup();
        using var transaction = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, PedalCalibrationReportTests.Collection, Original), device, device, recovery, TimeSpan.FromSeconds(1), "PE001700");
        var backup = await transaction.CreateHardwareBackupAsync();
        Assert.Empty(device.Writes);
        Assert.True(backup.IsManualBackup);
        Assert.Equal(Original, backup.Saved);
        var newer = Original.WithRange(PedalChannel.Brake, new(300, 1800));
        await transaction.ChangeAsync(newer);
        await transaction.SaveAsync();
        var writes = device.Writes.Count;
        await transaction.RestoreHardwareBackupAsync(backup);
        Assert.Equal(Original, device.State);
        Assert.Equal(newer, transaction.Saved);
        Assert.True(transaction.IsDirty);
        Assert.Equal(writes + 1, device.Writes.Count);
        Assert.Equal(3, device.Writes[^1][0]);
        Assert.Equal(newer, recovery.Snapshot!.Saved);
        Assert.Equal(Original, backup.Saved);
        await transaction.RestoreSavedAsync();
        Assert.Equal(newer, device.State);
    }

    [Fact]
    public async Task ManualBackupRestoreRejectsAnotherControllerAndAutomaticRecoveryDocuments()
    {
        var device = new Device();
        using var transaction = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, PedalCalibrationReportTests.Collection, Original), device, device, new Backup(), TimeSpan.FromSeconds(1), "PE001700");
        var backup = await transaction.CreateHardwareBackupAsync();
        await Assert.ThrowsAsync<IOException>(() => transaction.RestoreHardwareBackupAsync(backup with { SerialNumber = "PE0017FF" }));
        await Assert.ThrowsAsync<ArgumentException>(() => transaction.RestoreHardwareBackupAsync(backup with { IsManualBackup = false }));
        Assert.Empty(device.Writes);
        Assert.Equal(Original, device.State);
    }

    private static PedalMonitorEvent Sample(PedalChannel channel, int value) => new(PedalConnectionState.Connected, TimeSpan.Zero, 1,
        new(channel == PedalChannel.Throttle ? value : 0, channel == PedalChannel.Brake ? value : 0, channel == PedalChannel.Clutch ? value : 0));

    private sealed class Device : IFeatureReportReader, IFeatureReportWriter
    {
        public PedalCalibration State { get; set; } = Original;
        public List<byte[]> Writes { get; } = [];
        public bool FailWriteAfterApply { get; set; }
        public bool DisconnectAfterWrite { get; set; }
        public bool Disconnected { get; set; }
        public bool FailSave { get; set; }

        public Task<byte[]> ReadAsync(HidDeviceInfo collection, byte reportId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Disconnected) { throw new IOException("Disconnected."); }
            Assert.Equal(3, reportId);
            return Task.FromResult(PedalCalibrationReport.Encode(State));
        }

        public Task WriteAsync(HidDeviceInfo collection, ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Disconnected) { throw new IOException("Disconnected."); }
            var bytes = report.ToArray();
            Writes.Add(bytes);
            if (bytes[0] == 3) { State = PedalCalibrationReport.Decode(bytes); }
            if (bytes[0] == 2 && FailSave) { throw new IOException("Save failed."); }
            if (DisconnectAfterWrite) { Disconnected = true; }
            if (FailWriteAfterApply) { FailWriteAfterApply = false; throw new IOException("Write failed after applying."); }
            return Task.CompletedTask;
        }
    }

    private sealed class Backup : IHardwareRecoveryStore
    {
        public HardwareRecoverySnapshot? Snapshot { get; private set; }
        public bool FailWrites { get; set; }
        public int WriteCount { get; private set; }
        public int? FailOnWrite { get; set; }
        public Task WriteAsync(HardwareRecoverySnapshot snapshot, CancellationToken cancellationToken)
        {
            WriteCount++;
            if (FailWrites || WriteCount == FailOnWrite) { throw new IOException("Storage failed."); }
            Snapshot = snapshot;
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken cancellationToken) { Snapshot = null; return Task.CompletedTask; }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public void Advance(long milliseconds) => timestamp += milliseconds;
    }
}
