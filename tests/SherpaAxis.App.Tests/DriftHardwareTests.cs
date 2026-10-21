using System.IO;
using System.Runtime.CompilerServices;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Hardware;
using SherpaAxis.App.Services.Input;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Tests;

public sealed class DriftHardwareTests
{
    private static readonly HidDeviceInfo Collection = new("synthetic-drift", 0x0483, 0xA2EA, 0x200, 0xFF00, 1, 0, 0, 17);
    private static readonly PedalCalibration Original = new(new(1000, 15000), new(3200500, 4700500), new(1000, 15000));

    [Fact]
    public async Task SavingTranslatedHardwareBoundsAlsoTranslatesItsSeparateRawReference()
    {
        var folder = TempFolder();
        try
        {
            var device = new Device();
            var references = new SensorReferenceStore(Path.Combine(folder, "references"));
            var originalRaw = new HardwareCalibrationRange(3100500, 4800500);
            await references.WriteAsync(new("PE001700", PedalChannel.Brake, originalRaw, Original.Brake));
            var corrected = Original.WithRange(PedalChannel.Brake, new(2900500, 4400500));
            var proposal = Proposal(corrected) with { RawOriginal = originalRaw };
            await new CorrectedCalibrationWriter(device.Connect, folder, references).SaveAsync(proposal, CancellationToken.None);
            Assert.Equal(new(2800500, 4500500), (await references.ReadAsync("PE001700", corrected))[Profiles.PedalAxis.Brake]);
            Assert.Equal(originalRaw, (await references.ReadAsync("PE001700", Original))[Profiles.PedalAxis.Brake]);
            Assert.Equal(1, device.Saves);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public async Task RawInputLoadsTheReferenceForTheSavedBoundsBeforeReplacingThemTemporarily()
    {
        var folder = TempFolder();
        try
        {
            var device = new Device();
            var references = new SensorReferenceStore(Path.Combine(folder, "references"));
            await references.WriteAsync(new("PE001700", PedalChannel.Brake, new(3100500, 4800500), Original.Brake));
            var engine = EnabledEngine();
            var input = new NativePedalInput(engine, Samples, device.Connect,
                () => new HardwareRecoveryStore(Path.Combine(folder, "raw.json")), references);
            await using var cursor = input.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
            Assert.True(await cursor.MoveNextAsync()); engine.Accept(cursor.Current);
            Assert.True(await cursor.MoveNextAsync()); engine.Accept(cursor.Current);
            Assert.Equal(3100500, engine.DriftState(Profiles.PedalAxis.Brake).RawMinimum);
            Assert.Equal(Original.Brake.Minimum, engine.DriftState(Profiles.PedalAxis.Brake).Minimum);
            Assert.Equal(PedalHardwareSettings.Defaults, device.State);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public async Task CenteredStartupReportsAreDiscardedBeforeDriftEvidenceAndOutput()
    {
        var folder = TempFolder();
        try
        {
            var device = new Device { Input = StartupSamples };
            var engine = EnabledEngine();
            engine.Begin(new("live-device-axis"));
            var input = new NativePedalInput(engine, Samples, device.Connect,
                () => new HardwareRecoveryStore(Path.Combine(folder, "raw.json")));
            await using var cursor = input.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
            Assert.True(await cursor.MoveNextAsync()); engine.Accept(cursor.Current);
            Assert.False(engine.CanPublishOutput);
            Assert.True(await cursor.MoveNextAsync());
            Assert.Equal(Original.Brake.Minimum, cursor.Current.Axes!.Value.Brake);
            engine.Accept(cursor.Current);
            Assert.Equal(0, engine.DriftState(Profiles.PedalAxis.Brake).Offset);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    private static async IAsyncEnumerable<PedalMonitorEvent> StartupSamples([EnumeratorCancellation] CancellationToken token)
    {
        yield return new(PedalConnectionState.Connected, TimeSpan.Zero, 1);
        yield return new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(1), 1, new(1000, 8388608, 1000));
        await Task.Delay(120, token);
        yield return new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(121), 1, new(1000, Original.Brake.Minimum, 1000));
    }

    [Fact]
    public async Task DisablingDriftRestoresHardwareBeforeNormalReportsResume()
    {
        var folder = TempFolder();
        try
        {
            var device = new Device();
            var engine = EnabledEngine();
            var normalStarted = false;
            IAsyncEnumerable<PedalMonitorEvent> Normal(CancellationToken token)
            {
                Assert.Equal(Original, device.State);
                Assert.False(engine.DriftState(Profiles.PedalAxis.Brake).RawAvailable);
                normalStarted = true;
                return Samples(token);
            }
            var input = new NativePedalInput(engine, Normal, device.Connect,
                () => new HardwareRecoveryStore(Path.Combine(folder, "raw.json")));
            await using var cursor = input.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
            Assert.True(await cursor.MoveNextAsync());
            var profile = DashboardEngine.DefaultProfile();
            engine.Configure(profile, profile.Axes.Keys.ToDictionary(axis => axis, _ => new Core.AxisSessionSettings(1000, .05)));
            Assert.True(await cursor.MoveNextAsync());
            Assert.True(normalStarted);
            Assert.Equal(0, device.Saves);
            Assert.False(File.Exists(Path.Combine(folder, "raw.json")));
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public async Task RawLeaseBacksUpBeforeChangingBoundsAndRestoresWithoutSavingFirmware()
    {
        var folder = TempFolder();
        try
        {
            var device = new Device();
            var engine = EnabledEngine();
            var input = new NativePedalInput(engine, Samples, device.Connect,
                () => new HardwareRecoveryStore(Path.Combine(folder, "raw.json")));
            engine.Begin(new("live-device-axis"));
            await using (var cursor = input.WatchAsync(CancellationToken.None).GetAsyncEnumerator())
            {
                Assert.True(await cursor.MoveNextAsync());
                Assert.Equal("PE001700", engine.OwnedControllerIdentity?.SerialNumber);
                Assert.Equal(PedalHardwareSettings.Defaults, device.State);
                var backup = await HardwareRecoveryStore.ReadAsync(Path.Combine(folder, "raw.json"));
                Assert.Equal(Original, backup.Saved);
                Assert.Equal(PedalHardwareSettings.Defaults, backup.LastConfirmed);
                engine.Accept(cursor.Current);
                Assert.True(await cursor.MoveNextAsync()); engine.Accept(cursor.Current);
                Assert.True(engine.DriftState(Profiles.PedalAxis.Brake).RawAvailable);
            }
            Assert.Equal(Original, device.State);
            Assert.Null(engine.OwnedControllerIdentity);
            Assert.Equal(0, device.Saves);
            Assert.Equal(1, device.Disposals);
            Assert.False(File.Exists(Path.Combine(folder, "raw.json")));
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public async Task CancellationDuringEntryRestoresBoundsAndReleasesTheConnection()
    {
        var folder = TempFolder();
        try
        {
            using var cancel = new CancellationTokenSource();
            var device = new Device { AfterBoundsWrite = () => cancel.Cancel() };
            var input = new NativePedalInput(EnabledEngine(), Samples, device.Connect,
                () => new HardwareRecoveryStore(Path.Combine(folder, "raw.json")));
            await using var cursor = input.WatchAsync(cancel.Token).GetAsyncEnumerator();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cursor.MoveNextAsync());
            Assert.Equal(Original, device.State);
            Assert.Equal(1, device.Disposals);
            Assert.Equal(0, device.Saves);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRestorationRetainsRecoveryAndNeverLeaksTheHeldConnection(bool cancelling)
    {
        var folder = TempFolder();
        try
        {
            using var cancel = new CancellationTokenSource();
            var device = new Device();
            var input = new NativePedalInput(EnabledEngine(), Samples, device.Connect,
                () => new HardwareRecoveryStore(Path.Combine(folder, "raw.json")));
            var cursor = input.WatchAsync(cancel.Token).GetAsyncEnumerator();
            Assert.True(await cursor.MoveNextAsync());
            device.FailRestore = true;
            if (cancelling) { cancel.Cancel(); await cursor.DisposeAsync(); }
            else { await Assert.ThrowsAsync<IOException>(() => cursor.DisposeAsync().AsTask()); }
            Assert.Equal(1, device.Disposals);
            Assert.True(File.Exists(Path.Combine(folder, "raw.json")));
            Assert.Equal(0, device.Saves);
            device.FailRestore = false;
            var recovery = new StartupHardwareRecovery(folder, async (_, token) =>
            {
                var connection = await device.Connect(token);
                return new(Collection, connection.Identity.SerialNumber, device, device, device);
            });
            var pending = Assert.Single(await recovery.CheckAsync());
            Assert.True(pending.CanRestore);
            await recovery.RestoreAsync(pending.Path);
            Assert.Equal(Original, device.State);
            Assert.Empty(await recovery.CheckAsync());
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public async Task OnlyExplicitSavePersistsCorrectedBoundsAndRetainsAManualBackup()
    {
        var folder = TempFolder();
        try
        {
            var device = new Device();
            var corrected = Original.WithRange(PedalChannel.Brake, new(2900500, 4400500));
            await new CorrectedCalibrationWriter(device.Connect, folder).SaveAsync(Proposal(corrected), CancellationToken.None);
            Assert.Equal(corrected, device.State);
            Assert.Equal(1, device.Saves);
            var saved = Assert.Single(Directory.GetFiles(folder, "*.json"));
            var backup = await HardwareRecoveryStore.ReadAsync(saved);
            Assert.True(backup.IsManualBackup);
            Assert.Equal(Original, backup.Saved);
            Assert.Equal(Original.Throttle, device.State.Throttle);
            Assert.Equal(Original.Clutch, device.State.Clutch);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedControllerOrCalibrationIsRejectedBeforeAnyWrite(bool identity)
    {
        var folder = TempFolder();
        try
        {
            var device = new Device();
            if (identity) { device.Serial = "PE0017FF"; }
            else { device.State = Original.WithRange(PedalChannel.Throttle, new(1100, 15000)); }
            await Assert.ThrowsAsync<IOException>(() => new CorrectedCalibrationWriter(device.Connect, folder)
                .SaveAsync(Proposal(Original.WithRange(PedalChannel.Brake, new(2900500, 4400500))), CancellationToken.None));
            Assert.Equal(0, device.Writes);
            Assert.Equal(0, device.Saves);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public async Task UncertainDeviceSaveRetainsBothRecoveryAndTheOriginalBackup()
    {
        var folder = TempFolder();
        try
        {
            var device = new Device { FailSave = true };
            await Assert.ThrowsAsync<IOException>(() => new CorrectedCalibrationWriter(device.Connect, folder)
                .SaveAsync(Proposal(Original.WithRange(PedalChannel.Brake, new(2900500, 4400500))), CancellationToken.None));
            var records = await Task.WhenAll(Directory.GetFiles(folder, "*.json").Select(path => HardwareRecoveryStore.ReadAsync(path)));
            Assert.Contains(records, record => record.IsManualBackup && record.Saved == Original);
            Assert.Contains(records, record => !record.IsManualBackup && record.SaveUncertain);
            Assert.Equal(1, device.Disposals);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public async Task FailedBackupPreventsEveryHardwareWrite()
    {
        var folder = TempFolder();
        Directory.CreateDirectory(folder);
        try
        {
            var blocked = Path.Combine(folder, "blocked");
            await File.WriteAllTextAsync(blocked, "not a directory");
            var device = new Device();
            var failure = await Record.ExceptionAsync(() => new CorrectedCalibrationWriter(device.Connect, blocked)
                .SaveAsync(Proposal(Original.WithRange(PedalChannel.Brake, new(2900500, 4400500))), CancellationToken.None));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal(0, device.Writes);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static CorrectedCalibration Proposal(PedalCalibration corrected) => new("PE001700", Original, corrected, Profiles.PedalAxis.Brake, Original.Brake);

    private static DashboardEngine EnabledEngine()
    {
        var engine = new DashboardEngine();
        new DashboardViewModel(engine).SetCorrectionMode("Auto");
        return engine;
    }
    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "SherpaAxis-drift-hardware-" + Guid.NewGuid().ToString("N"));
    private static async IAsyncEnumerable<PedalMonitorEvent> Samples([EnumeratorCancellation] CancellationToken token)
    {
        yield return new(PedalConnectionState.Connected, TimeSpan.Zero, 1);
        for (var index = 0; ; index++)
        {
            token.ThrowIfCancellationRequested();
            yield return new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(index * 2), 1, new(1000, 3200500, 1000));
            await Task.Yield();
        }
    }
    private sealed class Device : IFeatureReportReader, IFeatureReportWriter, IAsyncDisposable
    {
        public Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> Input { get; init; } = Samples;
        public PedalCalibration State { get; set; } = Original;
        public string Serial { get; set; } = "PE001700";
        public int Saves { get; private set; }
        public int Writes { get; private set; }
        public int Disposals { get; private set; }
        public bool FailRestore { get; set; }
        public bool FailSave { get; init; }
        public Action? AfterBoundsWrite { get; init; }
        public Task<PedalControllerConnection> Connect(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new PedalControllerConnection(new(1, DateTimeOffset.UtcNow, Collection, State),
                new("Test", "Pedals", Serial), this, this, Input, this));
        }
        public Task<byte[]> ReadAsync(HidDeviceInfo collection, byte reportId, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(PedalCalibrationReport.Encode(State)); }
        public Task WriteAsync(HidDeviceInfo collection, ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            if (report.Span[0] == 2)
            { Saves++; if (FailSave) { throw new IOException("Save acknowledgement lost"); } }
            else
            {
                var target = PedalCalibrationReport.Decode(report.Span);
                if (FailRestore && target == Original) { throw new IOException("Cannot restore controller"); }
                State = target;
                AfterBoundsWrite?.Invoke();
            }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
