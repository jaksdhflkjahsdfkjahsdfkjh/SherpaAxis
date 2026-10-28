using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Hardware;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Bridge;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class StartupRecoveryTests
{
    private static readonly PedalCalibration Original = new(new(100, 1000), new(200, 2000), new(300, 3000));
    private static readonly PedalCalibration Temporary = Original.WithRange(PedalChannel.Brake, PedalHardwareSettings.Defaults.Brake);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoutineChecksStayQuietAndExistingProblemsRemainVisibleDuringRetries(bool failedScan)
    {
        var model = new StartupRecoveryViewModel();
        model.Checking();
        Assert.True(model.IsBlocked && model.Busy);
        Assert.False(model.NeedsAttention);
        model.Apply([]);
        Assert.False(model.IsBlocked || model.Busy || model.NeedsAttention);

        model.Checking();
        if (failedScan) { model.Fail("Controller unavailable"); }
        else { model.Apply([new("pending.json", null, "Interrupted calibration", false, false)]); }
        Assert.True(model.IsBlocked && model.NeedsAttention);
        Assert.False(model.Busy);
        model.Checking();
        Assert.True(model.IsBlocked && model.Busy && model.NeedsAttention);
        model.Apply([]);
        Assert.False(model.IsBlocked || model.Busy || model.NeedsAttention);
        model.Checking();
        Assert.True(model.IsBlocked && model.Busy);
        Assert.False(model.NeedsAttention);
    }

    [Fact]
    public Task RoutineRecoveryChecksNeverOpenTheBannerOrMoveTheGraphDuringCalibrationSwitches() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Device.State = Original;
        MainWindow? window = null;
        Rect? graphBounds = null;
        var checks = 0;
        void CheckLayout()
        {
            var main = Assert.IsType<MainWindow>(window);
            var content = Assert.IsAssignableFrom<FrameworkElement>(main.Content);
            content.Measure(new Size(1340, 900));
            content.Arrange(new Rect(0, 0, 1340, 900));
            content.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(main.FindName("StartupRecoveryPanel")).Visibility);
            var graph = Assert.IsType<Controls.SignalPlot>(main.FindName("MainSignalPlot"));
            var bounds = new Rect(graph.TranslatePoint(new Point(), content), new Size(graph.ActualWidth, graph.ActualHeight));
            graphBounds ??= bounds;
            Assert.Equal(graphBounds.Value, bounds);
        }
        var service = new StartupHardwareRecovery(fixture.Directory, async (_, _) =>
        {
            checks++;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var recovery = Assert.IsType<DashboardViewModel>(window!.DataContext).Recovery;
            Assert.True(recovery.IsBlocked && recovery.Busy);
            CheckLayout();
            return new(fixture.Device.Collection, fixture.Device.Serial, fixture.Device, fixture.Device, new Transport(() => { }));
        });
        await using var main = new MainWindow(new DashboardEngine(), (channel, theme, _) =>
        {
            var configuration = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, fixture.Device.Collection, Original),
                fixture.Device, fixture.Device, new HardwareRecoveryStore(fixture.Path), TimeSpan.FromSeconds(1));
            return Task.FromResult(new HardwareCalibrationSession(configuration, new(configuration, token => Input(() => { }, token), theme, selectedChannel: channel)));
        }, new SettingsAndTrayTests.FakeDesktop().Services with { Recovery = service });
        window = main;
        await main.InitializeForLaunchAsync();
        CheckLayout();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            await fixture.Journal();
            await main.RequestPanelModeAsync(SidePanelMode.Calibration);
            Assert.True(model.IsCalibrationMode);
            CheckLayout();
            Assert.Equal(cycle * 2, checks);
            await main.StartHardwareOperationAsync(HardwareCalibrationAction.Restore);
            CheckLayout();
            await fixture.Journal();
            await main.RequestPanelModeAsync(SidePanelMode.Tuning);
            Assert.False(model.IsCalibrationMode || model.Recovery.IsBlocked || model.Recovery.NeedsAttention);
            CheckLayout();
        }
        Assert.Equal(6, checks);
        Assert.Empty(fixture.Device.Writes);
    });

    [Fact]
    public async Task EmptyFolderAndManualBackupsDoNotOpenHardwareOrBlockStartup()
    {
        using var fixture = new Fixture();
        Assert.Empty(await fixture.Service.CheckAsync());
        await new HardwareRecoveryStore(fixture.Path).WriteAsync(new(2, DateTimeOffset.UtcNow, fixture.Device.Collection,
            Original, Original, null, SerialNumber: fixture.Device.Serial, IsManualBackup: true), CancellationToken.None);
        Assert.Empty(await fixture.Service.CheckAsync());
        Assert.Equal(0, fixture.Opens);
        Assert.True(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task AFullBackupArchiveDoesNotHideAnUnfinishedRecoveryOrBlockHealthyStartup()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 150; index++)
        {
            await new HardwareRecoveryStore(System.IO.Path.Combine(fixture.Directory, $"backup-{index}.json"))
                .WriteAsync(new(2, DateTimeOffset.UtcNow, fixture.Device.Collection, Original, Original, null,
                    SerialNumber: fixture.Device.Serial, IsManualBackup: true), CancellationToken.None);
        }
        Assert.Empty(await fixture.Service.CheckAsync());
        Assert.Equal(0, fixture.Opens);
        await fixture.Journal();
        var pending = Assert.Single(await fixture.Service.CheckAsync());
        Assert.Equal(fixture.Path, pending.Path);
        Assert.True(pending.CanRestore);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task VerifiedOriginalBoundsClearAStaleAutomaticJournalWithoutAnyWrites()
    {
        using var fixture = new Fixture();
        fixture.Device.State = Original;
        await fixture.Journal();
        Assert.Empty(await fixture.Service.CheckAsync());
        Assert.False(File.Exists(fixture.Path));
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(fixture.Opens, fixture.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawMonitoringRecoveryExplainsTheSessionWithoutClaimingUserCalibrationOrWritingDuringChecks(bool alreadyRestored)
    {
        using var fixture = new Fixture();
        var path = System.IO.Path.Combine(fixture.Directory, $"raw-{Guid.NewGuid():N}.json");
        fixture.Device.State = alreadyRestored ? Original : PedalHardwareSettings.Defaults;
        await new HardwareRecoveryStore(path).WriteAsync(new(1, DateTimeOffset.UtcNow, fixture.Device.Collection,
            Original, PedalHardwareSettings.Defaults, null, SerialNumber: fixture.Device.Serial), CancellationToken.None);
        var pending = await fixture.Service.CheckAsync();
        Assert.Empty(fixture.Device.Writes);
        if (alreadyRestored)
        {
            Assert.Empty(pending);
            Assert.False(File.Exists(path));
        }
        else
        {
            var record = Assert.Single(pending);
            Assert.True(record.CanRestore);
            Assert.Contains("Automatic drift monitoring was interrupted", record.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("An interrupted calibration", record.Detail, StringComparison.Ordinal);
            var model = new StartupRecoveryViewModel();
            model.Apply(pending);
            Assert.Equal("Unfinished drift fix needs recovery. Pedal input and virtual output are paused.", model.Status);
            Assert.True(model.IsBlocked && model.NeedsAttention);
            Assert.Equal(PedalHardwareSettings.Defaults, fixture.Device.State);
            await fixture.Service.RestoreAsync(path);
            Assert.Equal(Original, fixture.Device.State);
            Assert.False(File.Exists(path));
            Assert.Equal(3, Assert.Single(fixture.Device.Writes)[0]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirtyOrInterruptedWriteRestoresOnlyOnRequestAndNeverSendsSave(bool attempted)
    {
        using var fixture = new Fixture();
        await fixture.Journal(attempted: attempted);
        var pending = Assert.Single(await fixture.Service.CheckAsync());
        Assert.True(pending.CanRestore); Assert.False(pending.CanSave);
        Assert.Equal(Temporary, fixture.Device.State);
        Assert.Empty(fixture.Device.Writes);
        fixture.Device.BeforeWrite = () => Assert.True(File.Exists(fixture.Path));
        await fixture.Service.RestoreAsync(fixture.Path);
        Assert.Equal(Original, fixture.Device.State);
        Assert.False(File.Exists(fixture.Path));
        Assert.Equal(3, Assert.Single(fixture.Device.Writes)[0]);
        Assert.Empty(await fixture.Service.CheckAsync());
        Assert.Equal(fixture.Opens, fixture.Disposals);
    }

    [Fact]
    public async Task SameSerialOnANewUsbPathCanRecoverButDifferentSerialOrCapabilitiesCannot()
    {
        using var fixture = new Fixture();
        await fixture.Journal();
        fixture.Device.Serial = "PE0017CCDD";
        Assert.False(Assert.Single(await fixture.Service.CheckAsync()).CanRestore);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RestoreAsync(fixture.Path));
        fixture.Device.Serial = "PE0017AABB";
        fixture.Device.Collection = fixture.Device.Collection with { FeatureReportLength = 18 };
        Assert.False(Assert.Single(await fixture.Service.CheckAsync()).CanRestore);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RestoreAsync(fixture.Path));
        fixture.Device.Collection = fixture.Device.Collection with { Path = "reconnected-path", FeatureReportLength = 17 };
        Assert.True(Assert.Single(await fixture.Service.CheckAsync()).CanRestore);
        await fixture.Service.RestoreAsync(fixture.Path);
        Assert.Equal(Original, fixture.Device.State);
        Assert.Equal(3, Assert.Single(fixture.Device.Writes)[0]);
    }

    [Fact]
    public async Task UnknownExternalSettingsAndChangedFilesNeverGetOverwritten()
    {
        using var fixture = new Fixture();
        await fixture.Journal();
        Assert.True(Assert.Single(await fixture.Service.CheckAsync()).CanRestore);
        fixture.Device.State = Original.WithRange(PedalChannel.Throttle, new(200, 900));
        Assert.False(Assert.Single(await fixture.Service.CheckAsync()).CanRestore);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RestoreAsync(fixture.Path));
        Assert.Empty(fixture.Device.Writes);
        Assert.True(File.Exists(fixture.Path));
        await File.WriteAllTextAsync(fixture.Path, "{ corrupted after display");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RestoreAsync(fixture.Path));
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task MissingControllerCorruptJournalAndMissingSerialStayBlockedWithoutWrites()
    {
        using var fixture = new Fixture();
        await fixture.Journal();
        fixture.Offline = true;
        Assert.False(Assert.Single(await fixture.Service.CheckAsync()).CanRestore);
        Assert.True(File.Exists(fixture.Path));
        fixture.Offline = false;
        var snapshot = await HardwareRecoveryStore.ReadAsync(fixture.Path);
        await new HardwareRecoveryStore(fixture.Path).WriteAsync(snapshot with { SerialNumber = null }, CancellationToken.None);
        var opens = fixture.Opens;
        Assert.False(Assert.Single(await fixture.Service.CheckAsync()).CanRestore);
        Assert.Equal(opens, fixture.Opens);
        await File.WriteAllTextAsync(fixture.Path, "not json");
        Assert.Null(Assert.Single(await fixture.Service.CheckAsync()).Snapshot);
        Assert.Equal(opens, fixture.Opens);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task SaveUncertaintySurvivesRestoreAndOnlyExplicitVerifiedSaveClearsIt()
    {
        using var fixture = new Fixture();
        await fixture.Journal(uncertain: true);
        Assert.False(Assert.Single(await fixture.Service.CheckAsync()).CanSave);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveRestoredAsync(fixture.Path));
        await fixture.Service.RestoreAsync(fixture.Path);
        Assert.Equal(Original, fixture.Device.State);
        Assert.True(File.Exists(fixture.Path));
        Assert.True((await HardwareRecoveryStore.ReadAsync(fixture.Path)).SaveUncertain);
        Assert.True(Assert.Single(await fixture.Service.CheckAsync()).CanSave);
        Assert.All(fixture.Device.Writes, bytes => Assert.Equal(3, bytes[0]));
        fixture.Device.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.SaveRestoredAsync(fixture.Path));
        Assert.True((await HardwareRecoveryStore.ReadAsync(fixture.Path)).SaveUncertain);
        Assert.Single(await fixture.Service.CheckAsync());
        fixture.Device.FailSave = false;
        await fixture.Service.SaveRestoredAsync(fixture.Path);
        Assert.False(File.Exists(fixture.Path));
        Assert.Empty(await fixture.Service.CheckAsync());
        Assert.Equal(2, fixture.Device.Writes.Count(bytes => bytes[0] == 2));
        Assert.Equal(Original, fixture.Device.Persisted);
    }

    [Fact]
    public async Task FailedRestoreAndFailedReadbackKeepTheRecoveryJournal()
    {
        using var fixture = new Fixture();
        await fixture.Journal();
        fixture.Device.FailWrite = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RestoreAsync(fixture.Path));
        Assert.True(File.Exists(fixture.Path));
        Assert.True(Assert.Single(await fixture.Service.CheckAsync()).CanRestore);
        fixture.Device.FailWrite = false; fixture.Device.FailReadback = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RestoreAsync(fixture.Path));
        Assert.True(File.Exists(fixture.Path));
        Assert.False(Assert.Single(await fixture.Service.CheckAsync()).CanRestore);
        fixture.Device.FailReadback = false; fixture.Device.FailRead = false;
        Assert.Empty(await fixture.Service.CheckAsync()); // Restored state is only released after a successful read.
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task MultipleRecordsRemainBlockedUntilEveryRecordIsResolvedAndCancellationRetainsFiles()
    {
        using var fixture = new Fixture();
        await fixture.Journal();
        var other = System.IO.Path.Combine(fixture.Directory, "another.json");
        await File.WriteAllTextAsync(other, "corrupt");
        Assert.Equal(2, (await fixture.Service.CheckAsync()).Count);
        await fixture.Service.RestoreAsync(fixture.Path);
        Assert.Equal(other, Assert.Single(await fixture.Service.CheckAsync()).Path);
        File.Delete(other);
        await fixture.Journal(); fixture.Device.State = Temporary;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.CheckAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RestoreAsync(fixture.Path, cancelled.Token));
        Assert.True(File.Exists(fixture.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StartupHoldsInputAndOutputShowsRecoveryEvenForTrayStartupAndResumesAfterTheRestoreButton(bool uncertain) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Journal(uncertain: uncertain);
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        desktop.Store.Value = new(StartInTray: true);
        var inputStarts = 0; var outputs = new ConcurrentQueue<Controller>(); var hardwareOpens = 0;
        var services = desktop.Services with
        {
            Recovery = fixture.Service,
            Runtime = new(token => Input(() => Interlocked.Increment(ref inputStarts), token),
            () => { var controller = new Controller(); outputs.Enqueue(controller); return controller; })
        };
        await using var main = new MainWindow(new DashboardEngine(), (_, _, _) =>
        { hardwareOpens++; throw new InvalidOperationException("A new hardware session must stay blocked."); }, services);
        await main.InitializeForLaunchAsync();
        main.Show(); main.UpdateLayout();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        Assert.True(model.Recovery.IsBlocked); Assert.False(main.ShouldStartHidden);
        Assert.Equal(Visibility.Visible, Assert.IsType<Border>(main.FindName("StartupRecoveryPanel")).Visibility);
        Assert.Equal(0, inputStarts); Assert.Empty(outputs); Assert.Empty(fixture.Device.Writes);
        await main.RequestPanelModeAsync(SidePanelMode.Calibration);
        Assert.Equal(0, hardwareOpens);
        var restore = Assert.IsType<Button>(main.FindName("RestoreRecoveryButton"));
        Assert.True(restore.IsEnabled);
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (uncertain)
        {
            await Until(() => model.Recovery.CanSave);
            Assert.True(model.Recovery.IsBlocked); Assert.Equal(0, inputStarts); Assert.Empty(outputs);
            Assert.All(fixture.Device.Writes, bytes => Assert.Equal(3, bytes[0]));
            var save = Assert.IsType<Button>(main.FindName("SaveRecoveryButton"));
            Assert.True(save.IsEnabled);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        await Until(() => !model.Recovery.IsBlocked && outputs.Any(controller => controller.Values.Any(value => value.Throttle > 0)));
        Assert.Equal(Original, fixture.Device.State);
        Assert.Equal(1, inputStarts);
        Assert.False(File.Exists(fixture.Path));
        main.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(main.FindName("StartupRecoveryPanel")).Visibility);
    });

    [Fact]
    public Task OutputCannotStartWhileTheStartupVerificationIsStillPendingAndExitCancelsTheCheck() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Journal();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StartupHardwareRecovery(fixture.Directory, async (_, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); });
        var starts = 0;
        var services = new SettingsAndTrayTests.FakeDesktop().Services with
        {
            Recovery = service,
            Runtime = new(token => Input(() => Interlocked.Increment(ref starts), token), () => new Controller())
        };
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: services);
        var starting = main.InitializeForLaunchAsync();
        await entered.Task;
        Assert.Equal(0, starts);
        Assert.True(Assert.IsType<DashboardViewModel>(main.DataContext).Recovery.IsBlocked);
        await main.DisposeAsync(); await starting;
        Assert.Equal(0, starts); Assert.True(File.Exists(fixture.Path));
    });

    [Fact]
    public Task FailedFolderScanBlocksStartupAndCheckAgainCanResumeAfterTheFolderIsFixed() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Path, "A file is occupying the recovery directory path.");
        var service = new StartupHardwareRecovery(fixture.Path, (_, _) => throw new InvalidOperationException("No device must be opened."));
        var starts = 0;
        var services = new SettingsAndTrayTests.FakeDesktop().Services with
        {
            Recovery = service,
            Runtime = new(token => Input(() => Interlocked.Increment(ref starts), token), () => new Controller())
        };
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: services);
        await main.InitializeForLaunchAsync();
        var recovery = Assert.IsType<DashboardViewModel>(main.DataContext).Recovery;
        Assert.True(recovery.IsBlocked); Assert.True(recovery.CanCheck);
        Assert.Contains("could not finish", recovery.Status, StringComparison.Ordinal);
        Assert.Equal(0, starts);
        File.Delete(fixture.Path); Directory.CreateDirectory(fixture.Path);
        await main.ResolveStartupRecoveryAsync(false, false);
        await Until(() => starts > 0);
        Assert.False(recovery.IsBlocked);
    });

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(10, deadline.Token); }
    }
    private static async IAsyncEnumerable<PedalMonitorEvent> Input(Action started, [EnumeratorCancellation] CancellationToken token)
    {
        started();
        for (var index = 0; ; index++)
        {
            token.ThrowIfCancellationRequested();
            yield return new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(index * 5), 1, new(4000, 1000, 1000));
            await Task.Delay(5, token);
        }
    }
    private sealed class Controller : IVirtualController
    {
        public ConcurrentQueue<PedalOutput> Values { get; } = new();
        public void Write(PedalOutput output) => Values.Enqueue(output);
        public void Dispose() { }
    }
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SherpaAxis-recovery-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "pedals-test.json");
        public Device Device { get; } = new();
        public int Opens { get; private set; }
        public int Disposals { get; private set; }
        public bool Offline { get; set; }
        public StartupHardwareRecovery Service { get; }
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Service = new(Directory, (_, token) =>
            {
                token.ThrowIfCancellationRequested(); Opens++;
                if (Offline) { throw new IOException("Controller disconnected."); }
                return Task.FromResult(new RecoveryControllerConnection(Device.Collection, Device.Serial, Device, Device, new Transport(() => Disposals++)));
            });
        }
        public Task Journal(bool uncertain = false, bool attempted = false) => new HardwareRecoveryStore(Path).WriteAsync(
            new(1, DateTimeOffset.UtcNow, Device.Collection, Original, attempted ? Original : Temporary,
                attempted ? Temporary : null, uncertain, Device.Serial), CancellationToken.None);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
    private sealed class Transport(Action disposed) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { disposed(); return ValueTask.CompletedTask; }
    }
    private sealed class Device : IFeatureReportReader, IFeatureReportWriter
    {
        public HidDeviceInfo Collection { get; set; } = new("original-path", 0x0483, 0xA2EA, 0x200, 0xFF00, 1, 0, 0, 17);
        public string Serial { get; set; } = "PE0017AABB";
        public PedalCalibration State { get; set; } = Temporary;
        public PedalCalibration? Persisted { get; private set; }
        public List<byte[]> Writes { get; } = [];
        public bool FailWrite { get; set; }
        public bool FailRead { get; set; }
        public bool FailReadback { get; set; }
        public bool FailSave { get; set; }
        public Action? BeforeWrite { get; set; }
        public Task<byte[]> ReadAsync(HidDeviceInfo collection, byte id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Assert.Equal(Collection, collection);
            if (FailRead) { throw new IOException("Simulated read failure."); }
            return Task.FromResult(PedalCalibrationReport.Encode(State));
        }
        public Task WriteAsync(HidDeviceInfo collection, ReadOnlyMemory<byte> report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Assert.Equal(Collection, collection); BeforeWrite?.Invoke();
            if (FailWrite) { throw new IOException("Simulated write failure."); }
            var bytes = report.ToArray(); Writes.Add(bytes);
            if (bytes[0] == 3) { State = PedalCalibrationReport.Decode(bytes); }
            else { Assert.Equal(2, bytes[0]); Assert.Equal(3, bytes[1]); Persisted = State; if (FailSave) { throw new IOException("Uncertain Save."); } }
            if (FailReadback) { FailRead = true; }
            return Task.CompletedTask;
        }
    }
}
