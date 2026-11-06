using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Hardware;
using SherpaAxis.App.Services.Runtime;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Bridge;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class CalibrationRestoreUiTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task RestoreFromAnotherUsbPortKeepsSaveAttachedAndPersistsOnlyOnSave(bool recoveryFile, bool sameBounds) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture();
        await test.StartAsync();
        var target = sameBounds ? Device.Original : Device.Target;
        var path = await test.WriteBackupAsync(target, recoveryFile);
        var source = await File.ReadAllBytesAsync(path);
        await test.Main.RestoreHardwareBackupFromFileAsync(path);
        Assert.Equal(target, test.Controller.State);
        Assert.Equal(Device.Original, test.Controller.Persisted);
        Assert.True(test.Model.HasSession && test.Model.CanSave);
        Assert.True(test.Runtime.IsPaused);
        test.Main.UpdateLayout();
        var saveGroup = Assert.IsType<GroupBox>(test.View.FindName("CalibrationSaveGroup"));
        Assert.Equal(Visibility.Visible, saveGroup.Visibility);
        Assert.True(Assert.IsType<Button>(test.View.FindName("SaveControllerCalibrationButton")).IsEnabled);
        UiRefinementTests.CapturePreview(saveGroup, "calibration-restored-save.png");
        Assert.Equal(source, await File.ReadAllBytesAsync(path));
        await test.Main.StartHardwareOperationAsync(HardwareCalibrationAction.Save);
        Assert.Equal(target, test.Controller.Persisted);
        Assert.False(test.Model.CanSave);
        await test.Main.RequestPanelModeAsync(SidePanelMode.Tuning);
        Assert.Equal(target, test.Controller.State);
        await test.UntilLiveAsync();
        await test.Main.RequestPanelModeAsync(SidePanelMode.Calibration);
        Assert.Equal(target.Brake.Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture), test.Model.Minimum);
        Assert.Equal(target.Brake.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture), test.Model.Maximum);
    });

    [Fact]
    public Task DiscardAfterRestoreReturnsToThePreRestoreCalibration() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture();
        await test.StartAsync();
        await test.Main.RestoreHardwareBackupFromFileAsync(await test.WriteBackupAsync(Device.Target));
        await test.Main.StartHardwareOperationAsync(HardwareCalibrationAction.Restore);
        Assert.Equal(Device.Original, test.Controller.State);
        Assert.Equal(Device.Original, test.Controller.Persisted);
        Assert.False(test.Model.CanSave);
    });

    [Fact]
    public Task RestoringDuringCaptureEndsCaptureAndShowsSaveWithoutOpeningAnotherSession() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture();
        await test.StartAsync();
        await test.Main.StartHardwareOperationAsync(HardwareCalibrationAction.Capture);
        Assert.True(test.Model.IsCapturing);
        await test.Main.RestoreHardwareBackupFromFileAsync(await test.WriteBackupAsync(Device.Target));
        Assert.Equal(1, test.Opens);
        Assert.False(test.Model.IsCapturing);
        Assert.True(test.Model.HasSession && test.Model.CanSave);
        Assert.Equal(Device.Target, test.Controller.State);
    });

    [Fact]
    public Task InvalidFileNeverStopsLiveInputOrOpensAHardwareSession() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture();
        await test.StartAsync();
        var path = Path.Combine(test.Directory, "invalid.json");
        await File.WriteAllTextAsync(path, "{}");
        await test.Main.RestoreHardwareBackupFromFileAsync(path);
        Assert.Equal(0, test.Opens);
        Assert.False(test.Runtime.IsPaused);
        Assert.True(test.Source.IsRunning);
        Assert.Empty(test.Controller.Writes);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task WrongIdentityOrCapabilitiesResumeLiveOutputWithoutChangingCalibration(bool capabilities) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture();
        await test.StartAsync();
        var path = await test.WriteBackupAsync(Device.Target, serial: capabilities ? Fixture.Serial : "PE0017FFFF",
            collection: capabilities ? Device.Collection with { Version = 0x201 } : null);
        await test.Main.RestoreHardwareBackupFromFileAsync(path);
        Assert.Equal(Device.Original, test.Controller.State);
        Assert.Empty(test.Controller.Writes);
        Assert.False(test.Model.HasSession);
        await test.UntilLiveAsync();
        Assert.Contains("identity and HID capabilities", test.Model.Status, StringComparison.Ordinal);
    });

    [Fact]
    public Task AFailedConnectionResumesLiveInput() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture { FailConnect = true };
        await test.StartAsync();
        await test.Main.RestoreHardwareBackupFromFileAsync(await test.WriteBackupAsync(Device.Target));
        Assert.Equal(Device.Original, test.Controller.State);
        Assert.False(test.Model.HasSession);
        await test.UntilLiveAsync();
    });

    [Fact]
    public Task AFailedWriteRollsBackBeforeResumingLiveOutput() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture();
        await test.StartAsync();
        test.Controller.FailNextChange = true;
        await test.Main.RestoreHardwareBackupFromFileAsync(await test.WriteBackupAsync(Device.Target));
        Assert.Equal(Device.Original, test.Controller.State);
        Assert.Equal(Device.Original, test.Controller.Persisted);
        Assert.False(test.Model.HasSession);
        Assert.Empty(System.IO.Directory.GetFiles(test.RecoveryDirectory, "*.json"));
        await test.UntilLiveAsync();
    });

    [Fact]
    public Task FailedRollbackKeepsTheSessionAndOutputPausedUntilDiscardSucceeds() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture();
        await test.StartAsync();
        test.Controller.FailAllChanges = true;
        await test.Main.RestoreHardwareBackupFromFileAsync(await test.WriteBackupAsync(Device.Target));
        Assert.True(test.Model.HasSession && test.Model.CanAbandon);
        Assert.False(test.Model.CanSave);
        Assert.True(test.Runtime.IsPaused);
        Assert.False(test.Source.IsRunning);
        Assert.NotEmpty(System.IO.Directory.GetFiles(test.RecoveryDirectory, "*.json"));
        test.Controller.FailAllChanges = false;
        await test.Main.StartHardwareOperationAsync(HardwareCalibrationAction.Restore);
        await test.Main.RequestPanelModeAsync(SidePanelMode.Tuning);
        Assert.Equal(Device.Original, test.Controller.State);
        await test.UntilLiveAsync();
    });

    [Fact]
    public Task InvalidFilePreservesAnExistingUnsavedCalibrationSession() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture();
        await test.StartAsync();
        await test.Main.RestoreHardwareBackupFromFileAsync(await test.WriteBackupAsync(Device.Target));
        var view = test.View;
        var path = Path.Combine(test.Directory, "invalid.json");
        await File.WriteAllTextAsync(path, "{}");
        await test.Main.RestoreHardwareBackupFromFileAsync(path);
        Assert.Same(view, test.View);
        Assert.Equal(1, test.Opens);
        Assert.Equal(Device.Target, test.Controller.State);
        Assert.True(test.Model.CanSave);
    });

    [Fact]
    public Task RetryingRestoreAfterARecoverableFailureUsesTheSameSession() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture();
        await test.StartAsync();
        var path = await test.WriteBackupAsync(Device.Target);
        test.Controller.FailAllChanges = true;
        await test.Main.RestoreHardwareBackupFromFileAsync(path);
        Assert.True(test.Model.HasSession && test.Model.CanAbandon);
        test.Controller.FailAllChanges = false;
        await test.Main.RestoreHardwareBackupFromFileAsync(path);
        Assert.Equal(1, test.Opens);
        Assert.Equal(Device.Target, test.Controller.State);
        Assert.True(test.Model.CanSave);
        Assert.False(test.Model.CanAbandon);
    });

    [Fact]
    public Task AnUncertainSaveMustBeResolvedExplicitlyBeforeImportingAnotherBackup() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture();
        await test.StartAsync();
        await test.Main.RestoreHardwareBackupFromFileAsync(await test.WriteBackupAsync(Device.Target));
        test.Controller.FailSave = true;
        await test.Main.StartHardwareOperationAsync(HardwareCalibrationAction.Save);
        Assert.True(test.Model.CanAbandon);
        var another = Device.Target.WithRange(PedalChannel.Throttle, new(200, 1400));
        await test.Main.RestoreHardwareBackupFromFileAsync(await test.WriteBackupAsync(another));
        Assert.Equal(Device.Original, test.Controller.State);
        Assert.Equal(Device.Target, test.Controller.Persisted);
        Assert.Equal("200", test.Model.Minimum);
        Assert.True(test.Model.CanSave);
        Assert.True(test.Runtime.IsPaused);
        Assert.Equal(1, test.Controller.Saves);
        Assert.Contains("before loading another backup", test.Model.Status, StringComparison.Ordinal);
        test.Controller.FailSave = false;
        await test.Main.StartHardwareOperationAsync(HardwareCalibrationAction.Save);
        Assert.Equal(Device.Original, test.Controller.Persisted);
        Assert.False(test.Model.CanSave);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SelectingPendingRecoveryRestoresItsCheckpointAndKeepsUncertainSavesBlocked(bool uncertain) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var test = new Fixture();
        await test.StartAsync();
        test.Controller.State = PedalHardwareSettings.Defaults;
        var path = Path.Combine(test.RecoveryDirectory, "raw-interrupted.json");
        var snapshot = new HardwareRecoverySnapshot(1, DateTimeOffset.UtcNow,
            Device.Collection with { Path = "old-usb-port" }, Device.Original, PedalHardwareSettings.Defaults,
            null, SaveUncertain: uncertain, SerialNumber: Fixture.Serial);
        await new HardwareRecoveryStore(path).WriteAsync(snapshot, CancellationToken.None);
        await test.Main.RestoreHardwareBackupFromFileAsync(path);
        Assert.Equal(Device.Original, test.Controller.State);
        var retained = Assert.Single(System.IO.Directory.GetFiles(test.RecoveryDirectory, "manual-restore-source-*.json"));
        Assert.True((await HardwareRecoveryStore.ReadAsync(retained)).IsManualBackup);
        if (uncertain)
        {
            Assert.False(test.Model.HasSession);
            Assert.True(test.Runtime.IsPaused);
            Assert.True(Assert.Single(test.Dashboard.Recovery.Records).CanSave);
            Assert.Equal(0, test.Controller.Saves);
            await test.Main.ResolveStartupRecoveryAsync(false, true);
            Assert.Equal(1, test.Controller.Saves);
            Assert.False(test.Dashboard.Recovery.IsBlocked);
            await test.UntilLiveAsync();
        }
        else
        {
            Assert.True(test.Model.HasSession && test.Model.CanSave);
            Assert.False(File.Exists(path));
            Assert.False(test.Dashboard.Recovery.IsBlocked);
        }
    });

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Serial = "PE0017001122";
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "SherpaAxis-restore-" + Guid.NewGuid().ToString("N"));
        public string RecoveryDirectory => Path.Combine(Directory, "recovery");
        public Device Controller { get; } = new();
        public MainWindow Main { get; }
        public DashboardViewModel Dashboard => (DashboardViewModel)Main.DataContext;
        public HardwarePedalViewModel Model => Dashboard.HardwareDisplay!;
        public HardwareCalibrationView View => (HardwareCalibrationView)((ContentControl)Main.FindName("CalibrationHost")).Content;
        public PedalRuntime Runtime => Field<PedalRuntime>("runtime");
        public DashboardSource Source => Field<DashboardSource>("source");
        public bool FailConnect { get; init; }
        public int Opens { get; private set; }
        private int outputWrites;

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(RecoveryDirectory);
            var desktop = new SettingsAndTrayTests.FakeDesktop();
            desktop.Store.Value = new(TourDismissed: true);
            var services = desktop.Services with
            {
                VendorControlRunning = () => false,
                Runtime = new(LiveInput, () => new Output(() => Interlocked.Increment(ref outputWrites))),
                Recovery = new(RecoveryDirectory, (_, _) => Task.FromResult(new RecoveryControllerConnection(
                    Device.Collection, Serial, Controller, Controller)))
            };
            MainWindow? window = null;
            Main = new(new DashboardEngine(), (channel, theme, _) =>
            {
                Opens++;
                if (FailConnect) { throw new IOException("Controller connection failed."); }
                var journal = Path.Combine(RecoveryDirectory, "pedals-" + Guid.NewGuid().ToString("N") + ".json");
                var configuration = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, Device.Collection, Controller.State),
                    Controller, Controller, new HardwareRecoveryStore(journal), TimeSpan.FromSeconds(1), Serial);
                var view = (HardwareCalibrationView)typeof(MainWindow).GetField("cachedCalibrationView", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                view.Reconnect(configuration, HardwareInput, new("FEEL-VR", "Pedals One", Serial), channel);
                return Task.FromResult(new HardwareCalibrationSession(configuration, view, backupPath: journal));
            }, services, new(Path.Combine(Directory, "profiles")))
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Normal };
            window = Main;
        }

        public async Task StartAsync()
        {
            await Main.InitializeForLaunchAsync();
            Main.Show();
            await UntilLiveAsync();
            await Main.RequestPanelModeAsync(SidePanelMode.Calibration);
        }

        public async Task<string> WriteBackupAsync(PedalCalibration target, bool recovery = false, string serial = Serial, HidDeviceInfo? collection = null)
        {
            var path = Path.Combine(Directory, "backup-" + Guid.NewGuid().ToString("N") + ".json");
            await new HardwareRecoveryStore(path).WriteAsync(new(recovery ? 1 : 2, DateTimeOffset.UtcNow,
                (collection ?? Device.Collection) with { Path = "old-usb-port" }, target, target, null,
                SerialNumber: serial, IsManualBackup: !recovery), CancellationToken.None);
            return path;
        }

        public async Task UntilLiveAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var previous = Volatile.Read(ref outputWrites);
            while (Runtime.IsPaused || !Source.IsRunning || Volatile.Read(ref outputWrites) <= previous)
            { await Task.Delay(10, timeout.Token); }
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }

        private T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Main)!;

        public async ValueTask DisposeAsync()
        {
            Controller.FailAllChanges = false;
            Controller.FailSave = false;
            await Main.DisposeAsync();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class Device : IFeatureReportReader, IFeatureReportWriter
    {
        public static HidDeviceInfo Collection { get; } = new("current-usb-port", 0x0483, 0xA2EA, 0x200, 0xFF00, 1, 0, 0, 17);
        public static PedalCalibration Original { get; } = new(new(100, 1000), new(200, 2000), new(300, 3000));
        public static PedalCalibration Target { get; } = Original.WithRange(PedalChannel.Brake, new(400, 2400));
        public PedalCalibration State { get; set; } = Original;
        public PedalCalibration Persisted { get; private set; } = Original;
        public List<byte[]> Writes { get; } = [];
        public bool FailNextChange { get; set; }
        public bool FailAllChanges { get; set; }
        public bool FailSave { get; set; }
        public int Saves { get; private set; }
        public Task<byte[]> ReadAsync(HidDeviceInfo collection, byte reportId, CancellationToken token) => Task.FromResult(PedalCalibrationReport.Encode(State));
        public Task WriteAsync(HidDeviceInfo collection, ReadOnlyMemory<byte> report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var bytes = report.ToArray();
            Writes.Add(bytes);
            if (bytes[0] == PedalCalibrationReport.ReportId)
            {
                if (FailAllChanges)
                {
                    if (Writes.Count == 1) { State = PedalCalibrationReport.Decode(bytes); }
                    throw new IOException("Controller write and rollback failed.");
                }
                State = PedalCalibrationReport.Decode(bytes);
                if (FailNextChange) { FailNextChange = false; throw new IOException("Controller write failed after transfer."); }
            }
            else if (bytes.SequenceEqual(PedalCommandReport.Encode(PedalDeviceCommand.SaveConfiguration)))
            {
                Persisted = State; Saves++;
                if (FailSave) { throw new IOException("Save acknowledgement was lost."); }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class Output(Action write) : IVirtualController
    {
        public void Write(PedalOutput output) => write();
        public void Dispose() { }
    }

    private static async IAsyncEnumerable<PedalMonitorEvent> LiveInput([EnumeratorCancellation] CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            yield return new(PedalConnectionState.Connected, clock.Elapsed, 1, new(100, 600, 300));
            await Task.Delay(2, token);
        }
    }

    private static async IAsyncEnumerable<PedalMonitorEvent> HardwareInput([EnumeratorCancellation] CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        yield break;
    }
}
