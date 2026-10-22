using System.IO;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Output;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Bridge;
using SherpaAxis.Devices;

namespace SherpaAxis.App.Tests;

public sealed class DeviceDiagnosticsTests
{
    [Fact]
    public void VendorGuardRejectsControllerAccessUntilTheVendorAppIsClosed()
    {
        var failure = Assert.Throws<IOException>(() => VendorControl.RequireClosed(() => true));
        Assert.Equal(VendorControl.ConflictMessage, failure.Message);
        VendorControl.RequireClosed(() => false);
    }

    [Fact]
    public async Task RunningVendorAppIsReportedSeparatelyFromControllerFirmware()
    {
        var running = true;
        var diagnostics = new DeviceDiagnostics(_ => Task.FromResult<HidDeviceIdentity?>(new("FEEL-VR", "Pedals", "PE001700")),
            () => Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Free, true, true)), () => 2, () => running);
        var result = await diagnostics.CheckAsync(CancellationToken.None);
        Assert.False(result.FirmwareWarning);
        Assert.Equal(VendorControl.ConflictMessage, Assert.Single(result.Checks, check => check.Title == "FEEL-VR Control").Detail);
        Assert.True(Assert.Single(result.Checks, check => check.Title == "FEEL-VR Control").NeedsAttention);
        running = false;
        var closed = await diagnostics.CheckAsync(CancellationToken.None);
        Assert.DoesNotContain(closed.Checks, check => check.NeedsAttention);
    }

    [Fact]
    public async Task ActiveControllerIdentityIsReusedWithoutOpeningAnotherHidHandle()
    {
        var reads = 0;
        var diagnostics = new DeviceDiagnostics(_ =>
        {
            reads++;
            return Task.FromException<HidDeviceIdentity?>(new IOException("Exclusive handle is already held"));
        }, () => Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Owned, true, true)), () => 2);
        HidDeviceIdentity? owned = new("FEEL-VR", "Pedals", "PE001700");
        var result = await diagnostics.CheckAsync(CancellationToken.None, () => owned);
        Assert.Equal(0, reads);
        Assert.False(result.FirmwareWarning);
        Assert.DoesNotContain(result.Checks, check => check.NeedsAttention);
        owned = null;
        var closed = await diagnostics.CheckAsync(CancellationToken.None, () => owned);
        Assert.Equal(1, reads);
        Assert.Contains(closed.Checks, check => check.Title == "Firmware" && check.NeedsAttention);
    }

    [Fact]
    public async Task OwnershipAcquiredDuringDiscoveryReplacesASharingFailureWithVerifiedIdentity()
    {
        HidDeviceIdentity? owned = null;
        var diagnostics = new DeviceDiagnostics(_ =>
        {
            owned = new("FEEL-VR", "Pedals", "PE001700");
            return Task.FromException<HidDeviceIdentity?>(new IOException("Acquired during discovery"));
        }, () => Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Free, true, true)), () => 2);
        var result = await diagnostics.CheckAsync(CancellationToken.None, () => owned);
        Assert.False(result.FirmwareWarning);
        Assert.DoesNotContain(result.Checks, check => check.NeedsAttention);
    }

    [Fact]
    public void NativeAxisCodesDoNotMistakeMissingAxesOrSdkFailuresForEnabledAxes()
    {
        Assert.True(VJoyController.DecodeAxisPresence(1));
        Assert.False(VJoyController.DecodeAxisPresence(0));
        Assert.False(VJoyController.DecodeAxisPresence(-10));
        Assert.Throws<IOException>(() => VJoyController.DecodeAxisPresence(-2));
        Assert.Throws<IOException>(() => VJoyController.DecodeAxisPresence(-9));
    }

    private static VJoyDeviceInfo[] Devices(params VJoyDeviceInfo[] values) => Enumerable.Range(1, 16)
        .Select(id => values.SingleOrDefault(value => value.Id == id) ?? new((uint)id, VJoyDeviceState.Missing, false, false)).ToArray();

    [Fact]
    public void SelectionSkipsBusyDevicesAndOtherControllerConfigurations()
    {
        var devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Busy, true, true), new(2, VJoyDeviceState.Free, true, false), new(3, VJoyDeviceState.Free, true, true));
        Assert.Equal(3u, VJoyDeviceSelection.Select(devices, null));
        Assert.Throws<IOException>(() => VJoyDeviceSelection.Select(devices, 1));
        Assert.Throws<IOException>(() => VJoyDeviceSelection.Select(devices, 2));
        Assert.Throws<IOException>(() => VJoyDeviceSelection.Select(devices, 4));
    }

    [Fact]
    public void AutomaticConnectionPersistsTheAcquiredIdAndDoesNotSwitchBindingsOnRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-vjoy-selection-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "device.txt");
        var devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Busy, true, true), new(2, VJoyDeviceState.Free, true, true));
        var connected = new List<uint>();
        var controller = new FakeController();
        try
        {
            var first = new AutomaticVJoy(path, () => devices, id => { connected.Add(id); return controller; });
            using (first.Connect()) { Assert.Equal(2u, first.SelectedId); }
            devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Free, true, true), new(2, VJoyDeviceState.Free, true, true));
            var restart = new AutomaticVJoy(path, () => devices, id => { connected.Add(id); return new FakeController(); });
            using (restart.Connect()) { Assert.Equal(2u, restart.SelectedId); }
            devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Free, true, true), new(2, VJoyDeviceState.Busy, true, true));
            Assert.Throws<IOException>(() => restart.Connect());
            Assert.Equal(new uint[] { 2, 2 }, connected);
            Assert.Equal("2", File.ReadAllText(path));
            Assert.Equal(0, controller.Writes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FailedAcquisitionDoesNotRememberAnIdAndInvalidSavedIdsAreNotSilentlyReplaced()
    {
        var path = Path.Combine(Path.GetTempPath(), "SherpaAxis-vjoy-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = new AutomaticVJoy(path, () => Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Free, true, true)), _ => throw new IOException("Acquisition raced"));
            Assert.Throws<IOException>(() => output.Connect());
            Assert.False(File.Exists(path));
            File.WriteAllText(path, "17");
            Assert.Throws<InvalidDataException>(() => new AutomaticVJoy(path, () => [], _ => new FakeController()).Connect());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SetupCreatesOnlyAnUnusedSlotAndVerifiesTheDriverAfterCreation()
    {
        var devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Busy, true, true));
        var created = new List<uint>();
        var setup = new VJoySetup(() => devices, (id, _) =>
        { created.Add(id); devices = Devices(devices[0], new(id, VJoyDeviceState.Free, true, true)); return Task.CompletedTask; });
        var result = await setup.ConfigureAsync(null, CancellationToken.None);
        Assert.True(result.Ready);
        Assert.Equal(new uint[] { 2 }, created);
        Assert.True((await setup.ConfigureAsync(null, CancellationToken.None)).Ready);
        Assert.Single(created);
    }

    [Fact]
    public async Task SetupPreservesBusyOrChangedRememberedDevicesAndHandlesFullSlots()
    {
        var created = 0;
        var devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Busy, true, true), new(2, VJoyDeviceState.Free, true, true));
        var setup = new VJoySetup(() => devices, (_, _) => { created++; return Task.CompletedTask; });
        Assert.False((await setup.ConfigureAsync(1, CancellationToken.None)).Ready);
        devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Free, false, false));
        Assert.False((await setup.ConfigureAsync(1, CancellationToken.None)).Ready);
        devices = Enumerable.Range(1, 16).Select(id => new VJoyDeviceInfo((uint)id, VJoyDeviceState.Busy, true, true)).ToArray();
        var full = await setup.ConfigureAsync(null, CancellationToken.None);
        Assert.False(full.Ready);
        Assert.False(full.RestartRequired);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task SetupDoesNotTreatHelperSuccessAsSuccessfulConfiguration()
    {
        var setup = new VJoySetup(() => Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Free, true, false)), (_, _) => Task.CompletedTask);
        var result = await setup.ConfigureAsync(null, CancellationToken.None);
        Assert.False(result.Ready);
        Assert.True(result.RestartRequired);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.ConfigureAsync(null, new CancellationToken(true)));
    }

    [Fact]
    public async Task SetupRecreatesTheRememberedSlotRatherThanChangingTheDeviceId()
    {
        var created = new List<uint>();
        var devices = Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Free, true, true));
        var setup = new VJoySetup(() => devices, (id, _) =>
        { created.Add(id); devices = Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Free, true, true), new(id, VJoyDeviceState.Free, true, true)); return Task.CompletedTask; });
        Assert.True((await setup.ConfigureAsync(3, CancellationToken.None)).Ready);
        Assert.Equal(new uint[] { 3 }, created);
    }

    [Fact]
    public void FailureToPersistTheDeviceSelectionReleasesTheAcquiredController()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-vjoy-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var controller = new FakeController();
        try
        {
            var output = new AutomaticVJoy(directory, () => Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Free, true, true)), _ => controller);
            Assert.Throws<UnauthorizedAccessException>(() => output.Connect());
            Assert.True(controller.Disposed);
            Assert.Equal(0, controller.Writes);
        }
        finally { Directory.Delete(directory); }
    }

    [Theory]
    [InlineData("PE001700", false, "supported")]
    [InlineData("PE001600", true, "update to 017")]
    [InlineData("PE001800", true, "before changing firmware")]
    [InlineData("WE001700", true, "Hardware WE0")]
    public async Task FirmwareChecksDistinguishOlderAndNewerVersions(string serial, bool warning, string detail)
    {
        var reads = 0;
        var diagnostics = new DeviceDiagnostics(_ => { reads++; return Task.FromResult<HidDeviceIdentity?>(new("FEEL-VR", "Pedals", serial)); },
            () => Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Owned, true, true)), () => 2);
        var result = await diagnostics.CheckAsync(CancellationToken.None);
        Assert.Equal(warning, result.FirmwareWarning);
        Assert.Contains(detail, result.Checks[0].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Checks.Skip(1), check => check.NeedsAttention);
        Assert.Contains(result.Checks, check => check.Detail.Contains("vJoy 2", StringComparison.Ordinal));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task MissingDriverAndUnreadableIdentityRemainActionableWithoutAnyAcquisition()
    {
        var diagnostics = new DeviceDiagnostics(_ => Task.FromException<HidDeviceIdentity?>(new IOException("Identity missing")),
            () => throw new DllNotFoundException("Install vJoy"), () => null);
        var result = await diagnostics.CheckAsync(CancellationToken.None);
        Assert.Equal(2, result.Checks.Count);
        Assert.All(result.Checks, check => Assert.True(check.NeedsAttention));
        var missing = new DeviceDiagnostics(_ => Task.FromResult<HidDeviceIdentity?>(null), () => Devices(), () => null);
        var disconnected = await missing.CheckAsync(CancellationToken.None);
        Assert.Contains("Connect the FEEL-VR", disconnected.Checks[0].Detail, StringComparison.Ordinal);
        Assert.Contains(disconnected.Checks, check => check.Title == "Virtual output setup" && check.NeedsAttention);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => missing.CheckAsync(new CancellationToken(true)));
    }

    private sealed class FakeController : IVirtualController
    {
        public int Writes { get; private set; }
        public bool Disposed { get; private set; }
        public void Write(PedalOutput output) => Writes++;
        public void Dispose() => Disposed = true;
    }
}

[Collection("WPF UI")]
public sealed class DeviceDiagnosticsUiTests
{
    [Fact]
    public Task SettingsSwitchesReuseControlsGraphAndChecksUntilExplicitRefresh() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var reads = 0;
        var diagnostics = new DeviceDiagnostics(_ =>
        { Interlocked.Increment(ref reads); return Task.FromResult<HidDeviceIdentity?>(new("FEEL-VR", "Pedals", "PE001700")); },
            () => [new(2, VJoyDeviceState.Free, true, true)], () => 2);
        await using var main = new MainWindow(new(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services with { Diagnostics = diagnostics });
        await main.RequestPanelModeAsync(SherpaAxis.App.Models.SidePanelMode.Settings);
        await main.EnsureDeviceDiagnosticsAsync();
        Assert.Equal(1, reads);
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        var checks = model.Diagnostics.Checks.ToArray();
        var tuning = main.FindName("TuningSidePanel");
        var settings = main.FindName("SettingsSidePanel");
        var graph = main.FindName("MainSignalPlot");
        var histories = model.DisplayHistories;
        var transitions = 0;
        model.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(DashboardViewModel.IsSwitchingMode)) { transitions++; } };
        for (var cycle = 0; cycle < 5; cycle++)
        {
            await main.RequestPanelModeAsync(SherpaAxis.App.Models.SidePanelMode.Tuning);
            await main.RequestPanelModeAsync(SherpaAxis.App.Models.SidePanelMode.Settings);
            await main.EnsureDeviceDiagnosticsAsync();
            Assert.Same(tuning, main.FindName("TuningSidePanel"));
            Assert.Same(settings, main.FindName("SettingsSidePanel"));
            Assert.Same(graph, main.FindName("MainSignalPlot"));
            Assert.Same(histories, model.DisplayHistories);
            Assert.False(model.Diagnostics.Busy);
            Assert.Equal(checks, model.Diagnostics.Checks);
        }
        Assert.Equal(1, reads);
        Assert.Equal(0, transitions);
        Assert.IsType<System.Windows.Controls.Button>(main.FindName("CheckDeviceStatusButton"))
            .RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await main.EnsureDeviceDiagnosticsAsync();
        Assert.Equal(2, reads);
        model.Recovery.Fail("Restoration failed");
        await main.EnsureDeviceDiagnosticsAsync();
        Assert.Equal(3, reads);
        Assert.True(Assert.Single(model.Diagnostics.Checks, check => check.Title == "Calibration recovery").NeedsAttention);
        model.Recovery.Apply([]);
        model.Firmware.Blocked = true;
        await main.EnsureDeviceDiagnosticsAsync();
        Assert.Equal(4, reads);
        Assert.False(Assert.Single(model.Diagnostics.Checks, check => check.Title == "Calibration recovery").NeedsAttention);
        Assert.True(Assert.Single(model.Diagnostics.Checks, check => check.Title == "Firmware update").NeedsAttention);
        model.Firmware.Blocked = false;
        await main.EnsureDeviceDiagnosticsAsync();
        Assert.Equal(5, reads);
        Assert.DoesNotContain(model.Diagnostics.Checks, check => check.Title == "Firmware update");
    });

    [Fact]
    public Task SettingsCacheSharesInFlightChecksAndRefreshesWhenVendorControlChanges() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var reads = 0;
        var running = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new TaskCompletionSource<HidDeviceIdentity?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new DeviceDiagnostics(_ => { Interlocked.Increment(ref reads); entered.TrySetResult(); return identity.Task; },
            () => [new(2, VJoyDeviceState.Free, true, true)], () => 2, () => running);
        await using var main = new MainWindow(new(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services with
        { Diagnostics = diagnostics, VendorControlRunning = () => running });
        var initial = main.EnsureDeviceDiagnosticsAsync();
        await main.RequestPanelModeAsync(SherpaAxis.App.Models.SidePanelMode.Settings);
        Assert.Same(initial, main.EnsureDeviceDiagnosticsAsync());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        running = true;
        main.RefreshVendorControlStatus();
        identity.SetResult(new("FEEL-VR", "Pedals", "PE001700"));
        await initial;
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        Assert.Equal(2, reads);
        Assert.True(Assert.Single(model.Diagnostics.Checks, check => check.Title == "FEEL-VR Control").NeedsAttention);
        await main.RequestPanelModeAsync(SherpaAxis.App.Models.SidePanelMode.Tuning);
        await main.RequestPanelModeAsync(SherpaAxis.App.Models.SidePanelMode.Settings);
        await main.EnsureDeviceDiagnosticsAsync();
        Assert.Equal(2, reads);
        running = false;
        main.RefreshVendorControlStatus();
        await main.EnsureDeviceDiagnosticsAsync();
        Assert.Equal(3, reads);
        Assert.False(Assert.Single(model.Diagnostics.Checks, check => check.Title == "FEEL-VR Control").NeedsAttention);
    });

    [Fact]
    public Task VendorConflictIsVisibleAtStartupAndAcrossPanelsAndClearsAfterClosing() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var running = true;
        var connections = 0;
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-vendor-warning-" + Guid.NewGuid().ToString("N"));
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        desktop.Store.Value = new(StartInTray: true);
        try
        {
            await using var main = new MainWindow(new(), (_, _, _) =>
            { connections++; return Task.FromException<SherpaAxis.App.Services.Hardware.HardwareCalibrationSession>(new IOException("Fake disconnected controller")); },
                desktop.Services with { VendorControlRunning = () => running }, new(directory));
            await main.InitializeForLaunchAsync();
            main.Opacity = 0; main.ShowInTaskbar = false; main.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            var warning = Assert.IsType<System.Windows.Controls.Border>(main.FindName("VendorControlWarning"));
            Assert.True(model.VendorControlRunning);
            Assert.False(main.ShouldStartHidden);
            Assert.Equal(System.Windows.Visibility.Visible, warning.Visibility);
            Assert.Contains("tray instance", Assert.IsType<System.Windows.Controls.TextBlock>(warning.Child).Text, StringComparison.Ordinal);
            await main.RequestPanelModeAsync(SherpaAxis.App.Models.SidePanelMode.Settings);
            Assert.Equal(System.Windows.Visibility.Visible, warning.Visibility);
            await main.RequestPanelModeAsync(SherpaAxis.App.Models.SidePanelMode.Calibration);
            Assert.Equal(0, connections);
            Assert.Equal(System.Windows.Visibility.Visible, warning.Visibility);
            running = false;
            main.RefreshVendorControlStatus();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            Assert.False(model.VendorControlRunning);
            Assert.Equal(System.Windows.Visibility.Collapsed, warning.Visibility);
            await main.RequestPanelModeAsync(SherpaAxis.App.Models.SidePanelMode.Calibration);
            Assert.Equal(1, connections);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });

    [Fact]
    public Task SettingsFirmwareCheckUsesTheActiveInputConnectionIdentity() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var reads = 0;
        var diagnostics = new DeviceDiagnostics(_ =>
        { reads++; return Task.FromException<HidDeviceIdentity?>(new IOException("Already open")); },
            () => [new(2, VJoyDeviceState.Owned, true, true)], () => 2);
        var engine = new SherpaAxis.App.Models.DashboardEngine();
        var fake = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(engine, desktopServices: fake.Services with { Diagnostics = diagnostics });
        engine.SetOwnedControllerIdentity(new("FEEL-VR", "Pedals", "PE001700"));
        await main.CheckDeviceDiagnosticsAsync();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        Assert.Equal(0, reads);
        Assert.False(Assert.Single(model.Diagnostics.Checks, check => check.Title == "Firmware").NeedsAttention);
        engine.SetOwnedControllerIdentity(null);
        await main.CheckDeviceDiagnosticsAsync();
        Assert.Equal(1, reads);
        Assert.True(Assert.Single(model.Diagnostics.Checks, check => check.Title == "Firmware").NeedsAttention);
    });

    [Fact]
    public Task SettingsChecksAndRetryAreBoundWithoutOpeningHardwareOrStealingFocus() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var checks = 0;
        var diagnostics = new DeviceDiagnostics(_ => { checks++; return Task.FromResult<HidDeviceIdentity?>(new("FEEL-VR", "Pedals", "PE001600")); },
            () => [new(2, VJoyDeviceState.Free, true, true)], () => null);
        var fake = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(new(), desktopServices: fake.Services with { Diagnostics = diagnostics });
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        await main.RequestPanelModeAsync(SherpaAxis.App.Models.SidePanelMode.Settings);
        await main.CheckDeviceDiagnosticsAsync();
        Assert.True(model.IsSettingsMode);
        Assert.True(model.Diagnostics.FirmwareWarning);
        Assert.Equal("Not connected", model.Status);
        Assert.True(checks >= 1);
        Assert.Contains(model.Diagnostics.Checks, check => check.Title == "Calibration recovery");
        var retry = Assert.IsType<System.Windows.Controls.Button>(main.FindName("CheckDeviceStatusButton"));
        var content = Assert.IsAssignableFrom<System.Windows.FrameworkElement>(main.Content);
        content.Measure(new(1340, 900)); content.Arrange(new(0, 0, 1340, 900)); content.UpdateLayout();
        Assert.True(retry.IsEnabled);
        retry.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await main.CheckDeviceDiagnosticsAsync();
        Assert.True(checks >= 2);
        Assert.False(model.Diagnostics.Busy);
    });
}
