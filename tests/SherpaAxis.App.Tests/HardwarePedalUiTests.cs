using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Services;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Bridge;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Tests;

[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiTestGroup;

[Collection("WPF UI")]
public sealed class HardwarePedalUiTests
{
    [Fact]
    public async Task CalibrationViewButtonsCalibrateCancelAndSaveThroughTheRealViewModel()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var theme = new MainWindow(new SherpaAxis.App.Models.DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
                    await using var cleanupTheme = theme;
                    var device = new Device();
                    var backup = new Backup();
                    using var configuration = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, Device.Collection, Device.Original), device, device, backup, TimeSpan.FromSeconds(1), "PE001700");
                    await using var window = new HardwareCalibrationView(configuration, EmptyInput, theme.Resources, new("Test", "Test pedals", "PE001700"));
                    var content = (FrameworkElement)window.Content;
                    content.Measure(new Size(720, 780));
                    content.Arrange(new Rect(0, 0, 720, 780));
                    content.UpdateLayout();
                    var model = Assert.IsType<HardwarePedalViewModel>(window.DataContext);
                    var capture = Assert.IsType<Button>(window.FindName("CaptureButton"));
                    capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => !model.Busy);
                    Assert.True(model.IsCapturing);
                    Assert.Equal("Done", capture.Content);
                    Assert.False(model.CanSelect || model.CanSave);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => window.CreateHardwareBackupAsync());
                    model.Accept(Sample(3_300_000));
                    model.Accept(Sample(4_800_000));
                    model.Accept(Sample(3_400_000));
                    model.Refresh();
                    Assert.Equal("0% = 3,300,000\n100% = 4,800,000\nSensor input: 3,400,000", model.RangeInfo);
                    Assert.Equal(PedalChannel.Brake, model.SelectedChannel);
                    Assert.Contains(model.Histories.Single(series => series.Axis == Profiles.PedalAxis.Brake).Samples, point => point.DeviceInput == 3_300_000);
                    Assert.Contains("sensor counts", model.GraphLegend, StringComparison.Ordinal);
                    model.GraphPaused = true;
                    var frozen = model.Histories;
                    model.Accept(Sample(3_500_000));
                    model.Refresh();
                    Assert.Same(frozen, model.Histories);
                    capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => !model.Busy);
                    Assert.False(model.IsCapturing);
                    Assert.Equal(new(3_375_000, 4_725_000), device.State.Brake);
                    Assert.True(configuration.IsDirty);
                    Assert.Equal("3375000", model.Minimum);
                    Assert.Equal("Controller output: —", model.RangeInfo);
                    Assert.False(model.GraphPaused);
                    Assert.All(model.Histories, series => Assert.Empty(series.Samples));
                    var save = FindButton(content, "Save to device");
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => !model.Busy);
                    Assert.False(configuration.IsDirty);
                    Assert.Equal(3, device.Writes[^1][1]);
                    capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => !model.Busy);
                    FindButton(content, "Cancel calibration").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => !model.Busy);
                    Assert.False(model.IsCapturing);
                    Assert.Equal(new(3_375_000, 4_725_000), device.State.Brake);
                    Assert.Contains("Firmware 017", model.IdentityInfo, StringComparison.Ordinal);
                    var writesBeforeBackup = device.Writes.Count;
                    var manualBackup = await window.CreateHardwareBackupAsync();
                    Assert.True(manualBackup.IsManualBackup);
                    Assert.Equal(device.State, manualBackup.Saved);
                    Assert.Equal(writesBeforeBackup, device.Writes.Count);
                    var tools = Assert.IsType<StackPanel>(Descendants(content).OfType<Expander>().Single(expander => Equals(expander.Header, "Device tools")).Content);
                    Assert.Contains(Assert.IsType<Button>(window.FindName("BackupHardwareButton")), tools.Children.OfType<Button>());
                    Assert.Contains(Assert.IsType<Button>(window.FindName("RestoreHardwareBackupButton")), tools.Children.OfType<Button>());
                    Assert.DoesNotContain(tools.Children.OfType<Button>(), button => button.Content is "LED on" or "LED off");
                    var backupRequests = 0;
                    window.BackupRequested += (_, _) => backupRequests++;
                    Assert.IsType<Button>(window.FindName("BackupHardwareButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(1, backupRequests);
                    capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => !model.Busy);
                    Assert.True(configuration.IsDirty);
                    model.Accept(new(PedalConnectionState.Disconnected, TimeSpan.Zero, 1));
                    model.Accept(Sample(500) with { ConnectionNumber = 2 });
                    model.Refresh();
                    Assert.Contains("Capture interrupted", model.GraphLegend, StringComparison.Ordinal);
                    Assert.All(model.Histories, series => Assert.Empty(series.Samples));
                    Assert.True(await window.TryCloseAsync());
                    Assert.Equal(new(3_375_000, 4_725_000), device.State.Brake);
                    Assert.False(configuration.IsDirty);
                    Assert.False(model.IsCapturing);
                    completion.SetResult();
                }
                catch (Exception failure) { completion.SetException(failure); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task HardwareRecoveryFileRoundTripsAndMalformedDataCannotBeUsed()
    {
        var parent = Path.Combine(Path.GetTempPath(), "SherpaAxis-hardware-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(parent, "backup.json");
        var store = new HardwareRecoveryStore(path);
        var snapshot = new HardwareRecoverySnapshot(1, DateTimeOffset.UtcNow, Device.Collection,
            Device.Original, PedalHardwareSettings.Defaults, Device.Original, SerialNumber: "PE001700");
        try
        {
            await store.WriteAsync(snapshot, CancellationToken.None);
            Assert.Equal(snapshot, await HardwareRecoveryStore.ReadAsync(path));
            await File.WriteAllTextAsync(path, "{bad json");
            await Assert.ThrowsAsync<InvalidDataException>(() => HardwareRecoveryStore.ReadAsync(path));
            var manual = snapshot with { SchemaVersion = 2, LastConfirmed = snapshot.Saved, Attempted = null, IsManualBackup = true };
            await store.WriteAsync(manual, CancellationToken.None);
            Assert.Equal(manual, await HardwareRecoveryStore.ReadAsync(path));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(manual with { SchemaVersion = 1 }, CancellationToken.None));
            Assert.Equal(manual, await HardwareRecoveryStore.ReadAsync(path));
            await store.WriteAsync(snapshot, CancellationToken.None);
            await store.ClearAsync(CancellationToken.None);
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.EnumerateFiles(parent));
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
            if (Directory.Exists(parent)) { Directory.Delete(parent); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SwitchingSidePanelsKeepsTheSameGraphAndRestoresUnsavedBounds(bool failRestoration) => OnUiAsync(async () =>
    {
        var device = new Device();
        var engine = new Models.DashboardEngine();
        HardwareCalibrationView? cachedView = null;
        var connections = 0;
        await using var main = new MainWindow(engine, (channel, theme, _) =>
        {
            connections++;
            var configuration = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, Device.Collection, Device.Original), device, device, new Backup(), TimeSpan.FromSeconds(1));
            if (cachedView is null) { cachedView = new(configuration, EmptyInput, theme, selectedChannel: channel); }
            else { cachedView.Reconnect(configuration, EmptyInput, selectedChannel: channel); }
            return Task.FromResult(new HardwareCalibrationSession(configuration, cachedView));
        }, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var dashboard = Assert.IsType<DashboardViewModel>(main.DataContext);
        dashboard.SelectedAxis = Profiles.PedalAxis.Throttle;
        var calibrationButton = Assert.IsType<Button>(main.FindName("CalibrationModeButton"));
        var tuningButton = Assert.IsType<Button>(main.FindName("TuningModeButton"));
        var host = Assert.IsType<ContentControl>(main.FindName("CalibrationHost"));
        var emptyCalibrationFrames = 0;
        dashboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DashboardViewModel.DisplayHistories) && dashboard.IsCalibrationMode && dashboard.HardwareDisplay is null)
            { emptyCalibrationFrames++; }
        };
        var content = (FrameworkElement)main.Content;
        content.Measure(new Size(1340, 900));
        content.Arrange(new Rect(0, 0, 1340, 900));
        content.UpdateLayout();
        var plot = Assert.IsType<Controls.SignalPlot>(main.FindName("MainSignalPlot"));
        var bounds = new Rect(plot.TranslatePoint(new Point(), content), new Size(plot.ActualWidth, plot.ActualHeight));
        calibrationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => host.Content is HardwareCalibrationView && !dashboard.IsSwitchingMode);
        var panel = Assert.IsType<HardwareCalibrationView>(host.Content);
        var captureButton = Assert.IsType<Button>(panel.FindName("CaptureButton"));
        var model = Assert.IsType<HardwarePedalViewModel>(panel.DataContext);
        Assert.Equal(PedalChannel.Throttle, model.SelectedChannel);
        content.Measure(new Size(1340, 900));
        content.Arrange(new Rect(0, 0, 1340, 900));
        content.UpdateLayout();
        Assert.Same(plot, main.FindName("MainSignalPlot"));
        Assert.Equal(bounds, new Rect(plot.TranslatePoint(new Point(), content), new Size(plot.ActualWidth, plot.ActualHeight)));
        Assert.True(dashboard.IsCalibrationMode);
        Assert.Equal("Device counts", plot.Mode);
        Assert.Null(panel.FindName("CalibrationSignalPlot"));
        Assert.True(plot.ActualWidth > 300 && plot.ActualHeight >= 220);
        model.GraphPaused = true;
        calibrationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(model.GraphPaused);
        Assert.False(dashboard.IsSwitchingMode);
        Assert.Equal(1, connections);
        Assert.IsType<Button>(panel.FindName("CaptureButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !model.Busy);
        Assert.Equal(PedalHardwareSettings.Defaults.Throttle, device.State.Throttle);
        Assert.True(model.IsCapturing);
        if (failRestoration)
        {
            device.FailWrites = true;
            tuningButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !dashboard.IsSwitchingMode && model.CanAbandon);
            Assert.True(dashboard.IsCalibrationMode);
            Assert.Same(panel, host.Content);
            Assert.Equal(PedalHardwareSettings.Defaults.Throttle, device.State.Throttle);
            device.FailWrites = false;
        }
        var restore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.WriteBarrier = restore.Task;
        tuningButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(dashboard.IsSwitchingMode && dashboard.IsCalibrationMode);
        Assert.Same(panel, host.Content);
        Assert.Equal(Visibility.Visible, host.Visibility);
        Assert.Equal("Device counts", dashboard.DisplayGraphMode);
        restore.SetResult();
        await UntilAsync(() => !dashboard.IsCalibrationMode && !dashboard.IsSwitchingMode);
        Assert.False(dashboard.IsCalibrationMode);
        Assert.Same(panel, host.Content);
        Assert.Equal(Visibility.Hidden, host.Visibility);
        Assert.Equal(Device.Original, device.State);
        Assert.All(device.Writes, bytes => Assert.Equal(3, bytes[0]));
        dashboard.GraphPaused = true;
        var message = dashboard.Message;
        tuningButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(dashboard.GraphPaused);
        Assert.Equal(message, dashboard.Message);
        calibrationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => dashboard.IsCalibrationMode && !dashboard.IsSwitchingMode);
        Assert.Equal(2, connections);
        Assert.Same(panel, host.Content);
        Assert.NotSame(model, panel.DataContext);
        Assert.Same(captureButton, cachedView!.FindName("CaptureButton"));
        Assert.Equal(0, emptyCalibrationFrames);
        Assert.Equal(Visibility.Hidden, Assert.IsType<DockPanel>(main.FindName("TuningSidePanel")).Visibility);
    });

    internal static async Task OnUiAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception failure) { completion.SetException(failure); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public Task DisposalDuringCalibrationConnectionWaitsAndReleasesTheCreatedSession() => OnUiAsync(async () =>
    {
        var connect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new TransportLifetime();
        var device = new Device();
        await using var main = new MainWindow(new Models.DashboardEngine(), async (channel, theme, _) =>
        {
            await connect.Task;
            var configuration = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, Device.Collection, Device.Original), device, device, new Backup(), TimeSpan.FromSeconds(1));
            return new(configuration, new(configuration, EmptyInput, theme, selectedChannel: channel), transport);
        }, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        Assert.IsType<Button>(main.FindName("CalibrationModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(Assert.IsType<DashboardViewModel>(main.DataContext).IsSwitchingMode);
        var disposal = main.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        connect.SetResult();
        await disposal;
        Assert.True(transport.Disposed);
        Assert.Null(Assert.IsType<ContentControl>(main.FindName("CalibrationHost")).Content);
        Assert.Empty(device.Writes);
    });

    private sealed class TransportLifetime : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HidingToTrayRestoresCalibrationOrKeepsRecoveryAvailable(bool failRestoration) => OnUiAsync(async () =>
    {
        var device = new Device();
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(new Models.DashboardEngine(), (channel, theme, _) =>
        {
            var config = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, Device.Collection, Device.Original), device, device, new Backup(), TimeSpan.FromSeconds(1));
            return Task.FromResult(new HardwareCalibrationSession(config, new(config, EmptyInput, theme, selectedChannel: channel)));
        }, desktop.Services)
        { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, WindowState = WindowState.Normal };
        await main.InitializeForLaunchAsync();
        await main.RequestPanelModeAsync(Models.SidePanelMode.Calibration);
        var dashboard = Assert.IsType<DashboardViewModel>(main.DataContext);
        var view = Assert.IsType<HardwareCalibrationView>(Assert.IsType<ContentControl>(main.FindName("CalibrationHost")).Content);
        Assert.IsType<Button>(view.FindName("CaptureButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !dashboard.HardwareDisplay!.Busy);
        try
        {
            device.FailWrites = failRestoration;
            Assert.Equal(!failRestoration, await main.HideToTrayAsync());
            if (failRestoration)
            {
                Assert.True(dashboard.IsCalibrationMode && main.IsVisible);
                Assert.True(dashboard.HardwareDisplay!.CanAbandon);
                device.FailWrites = false;
                Assert.True(await main.HideToTrayAsync());
            }
            Assert.Equal(Device.Original, device.State);
            Assert.False(main.IsVisible || dashboard.IsCalibrationMode);
            var plot = main.FindName("MainSignalPlot");
            await main.RequestPanelModeAsync(Models.SidePanelMode.Settings);
            Assert.True(dashboard.IsSettingsMode);
            Assert.Same(plot, main.FindName("MainSignalPlot"));
        }
        finally { device.FailWrites = false; }
    });

    [Fact]
    public Task OneCloseRequestCancelsAnActiveOperationAndClosesTheWindow() => OnUiAsync(async () =>
    {
        await using var main = new MainWindow(new SherpaAxis.App.Models.DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var lifetime = MainField<CancellationTokenSource>(main, "lifetime");
        var pending = Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
        SetMainField(main, "activeOperation", pending);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Closed += (_, _) => closed.TrySetResult();
        main.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pending.IsCanceled);
        Assert.False(MainField<DispatcherTimer>(main, "refresh").IsEnabled);
    });

    [Fact]
    public Task ShutdownStopsInputAndOutputTogetherAndRepeatedRequestsShareCleanup() => OnUiAsync(async () =>
    {
        await using var main = new MainWindow(new SherpaAxis.App.Models.DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var inputStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = MainField<DashboardSource>(main, "source");
        var controller = new ClosingController();
        var runtime = new PedalRuntime(MainField<Models.DashboardEngine>(main, "engine"), source, new(Input, () => controller), new ButtonStateBuffer());
        SetMainField(main, "runtime", runtime);
        try
        {
            await runtime.ResumeAsync();
            await Task.Delay(30);
            var first = main.DisposeAsync().AsTask();
            var repeated = main.DisposeAsync().AsTask();
            await inputStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await controller.Closing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(first.IsCompleted || repeated.IsCompleted);
            Assert.False(MainField<DispatcherTimer>(main, "refresh").IsEnabled);
            releaseInput.TrySetResult();
            controller.Release.TrySetResult();
            await Task.WhenAll(first, repeated).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(source.IsRunning);
            Assert.Equal(1, controller.CloseCount);
            Assert.Equal(default, controller.Last);
        }
        finally { releaseInput.TrySetResult(); controller.Release.TrySetResult(); }

        async IAsyncEnumerable<PedalMonitorEvent> Input([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                while (true) { yield return Sample(200) with { Elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64) }; await Task.Delay(2, token); }
            }
            finally { inputStopped.TrySetResult(); await releaseInput.Task; }
        }
    });

    [Fact]
    public Task FailedShutdownKeepsRecoveryUsableAndCanBeRetried() => OnUiAsync(async () =>
    {
        var device = new Device();
        await using var main = new MainWindow(new Models.DashboardEngine(), (channel, theme, _) =>
        {
            var config = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, Device.Collection, Device.Original), device, device, new Backup(), TimeSpan.FromSeconds(1));
            return Task.FromResult(new HardwareCalibrationSession(config, new(config, EmptyInput, theme, selectedChannel: channel)));
        }, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var dashboard = Assert.IsType<DashboardViewModel>(main.DataContext);
        Assert.IsType<Button>(main.FindName("CalibrationModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => dashboard.HardwareDisplay is not null && !dashboard.IsSwitchingMode);
        var panel = Assert.IsType<HardwareCalibrationView>(Assert.IsType<ContentControl>(main.FindName("CalibrationHost")).Content);
        var model = Assert.IsType<HardwarePedalViewModel>(panel.DataContext);
        Assert.IsType<Button>(panel.FindName("CaptureButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !model.Busy);
        device.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => main.DisposeAsync().AsTask());
        Assert.True(dashboard.CanEdit && dashboard.IsCalibrationMode && model.CanAbandon);
        Assert.False(MainField<CancellationTokenSource>(main, "lifetime").IsCancellationRequested);
        device.FailWrites = false;
        await main.DisposeAsync();
        Assert.Equal(Device.Original, device.State);
    });

    private static T MainField<T>(MainWindow main, string name) => Assert.IsType<T>(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main));
    private static void SetMainField(MainWindow main, string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);

    private sealed class ClosingController : IVirtualController
    {
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CloseCount { get; private set; }
        public PedalOutput Last { get; private set; }
        public void Write(PedalOutput output) => Last = output;
        public void Dispose() { CloseCount++; Closing.TrySetResult(); Release.Task.GetAwaiter().GetResult(); }
    }

    [Fact]
    public Task RapidModeRequestsCancelPendingConnectionAndTheLastClickWinsWithoutMovingTheGraph() => OnUiAsync(async () =>
    {
        var connect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new Device();
        var transports = new List<TransportLifetime>();
        var calls = 0;
        await using var main = new MainWindow(new Models.DashboardEngine(), async (channel, theme, _) =>
        {
            calls++;
            if (calls == 1) { await connect.Task; }
            var transport = new TransportLifetime();
            transports.Add(transport);
            var config = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, Device.Collection, Device.Original), device, device, new Backup(), TimeSpan.FromSeconds(1));
            return new(config, new(config, EmptyInput, theme, selectedChannel: channel), transport);
        }, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        var calibration = Assert.IsType<Button>(main.FindName("CalibrationModeButton"));
        var tuning = Assert.IsType<Button>(main.FindName("TuningModeButton"));
        calibration.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(model.IsCalibrationMode);
        Assert.True(model.IsSwitchingMode);
        Assert.Equal(Visibility.Visible, Assert.IsType<DockPanel>(main.FindName("TuningSidePanel")).Visibility);
        Assert.Equal(Visibility.Hidden, Assert.IsType<Grid>(main.FindName("CalibrationSidePanel")).Visibility);
        Assert.Equal(model.GraphMode, model.DisplayGraphMode);
        await Task.Yield();
        Assert.True(tuning.IsEnabled);
        tuning.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(model.IsCalibrationMode);
        calibration.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(model.IsCalibrationMode);
        connect.SetResult();
        await UntilAsync(() => model.HardwareDisplay is not null && !model.IsSwitchingMode);
        Assert.Equal(2, calls);
        Assert.True(transports[0].Disposed);
        Assert.False(transports[1].Disposed);
        tuning.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !model.IsCalibrationMode && !model.IsSwitchingMode);
        Assert.All(transports, transport => Assert.True(transport.Disposed));
        Assert.Empty(device.Writes);
    });

    private static async Task UntilAsync(Func<bool> ready)
    {
        for (var i = 0; i < 100 && !ready(); i++) { await Task.Delay(5); }
        Assert.True(ready());
    }

    [Fact]
    public Task FailedConnectionKeepsCurrentPanelUntilErrorIsReadyAndCanBeRetried() => OnUiAsync(async () =>
    {
        var connect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var device = new Device();
        await using var main = new MainWindow(new Models.DashboardEngine(), async (channel, theme, _) =>
        {
            if (++calls == 1) { await connect.Task; throw new IOException("Test controller unavailable"); }
            var config = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, Device.Collection, Device.Original), device, device, new Backup(), TimeSpan.FromSeconds(1));
            return new(config, new(config, EmptyInput, theme, selectedChannel: channel));
        }, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var dashboard = Assert.IsType<DashboardViewModel>(main.DataContext);
        var calibration = Assert.IsType<Button>(main.FindName("CalibrationModeButton"));
        calibration.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(dashboard.IsCalibrationMode);
        Assert.Equal(Visibility.Visible, Assert.IsType<DockPanel>(main.FindName("TuningSidePanel")).Visibility);
        connect.SetResult();
        await UntilAsync(() => !dashboard.IsSwitchingMode);
        Assert.True(dashboard.IsCalibrationMode);
        Assert.Equal("Test controller unavailable", Assert.IsType<TextBlock>(main.FindName("CalibrationConnectionInfo")).Text);
        calibration.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => dashboard.HardwareDisplay is not null && !dashboard.IsSwitchingMode);
        Assert.Equal(2, calls);
        Assert.Equal(Visibility.Visible, Assert.IsType<ContentControl>(main.FindName("CalibrationHost")).Visibility);
        Assert.Empty(device.Writes);
    });

    private static async IAsyncEnumerable<PedalMonitorEvent> EmptyInput([EnumeratorCancellation] CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        yield break;
    }

    private static PedalMonitorEvent Sample(int brake) => new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, brake, 0));
    private static Button FindButton(DependencyObject parent, string text) => Descendants(parent).OfType<Button>().Single(b => Equals(b.Content, text));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }

    private sealed class Device : IFeatureReportReader, IFeatureReportWriter
    {
        public static HidDeviceInfo Collection { get; } = new("synthetic-pedal", 0x0483, 0xA2EA, 0x200, 0xFF00, 1, 0, 0, 17);
        public static PedalCalibration Original { get; } = new(new(100, 1000), new(200, 2000), new(300, 3000));
        public PedalCalibration State { get; private set; } = Original;
        public List<byte[]> Writes { get; } = [];
        public bool FailWrites { get; set; }
        public Task? WriteBarrier { get; set; }
        public Task<byte[]> ReadAsync(HidDeviceInfo collection, byte reportId, CancellationToken cancellationToken) => Task.FromResult(PedalCalibrationReport.Encode(State));
        public async Task WriteAsync(HidDeviceInfo collection, ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
        {
            var bytes = report.ToArray();
            Writes.Add(bytes);
            if (WriteBarrier is { } pending) { await pending.WaitAsync(cancellationToken); }
            if (FailWrites) { throw new IOException("Simulated controller restoration failure."); }
            if (bytes[0] == 3) { State = PedalCalibrationReport.Decode(bytes); }
        }
    }

    private sealed class Backup : IHardwareRecoveryStore
    {
        public Task WriteAsync(HardwareRecoverySnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
