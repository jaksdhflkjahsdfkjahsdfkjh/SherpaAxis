using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.App.Services.Hardware;
using SherpaAxis.App.Services.Input;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.Services.Runtime;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Bridge;
using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class AutomaticPedalsTests
{
    [Fact]
    public Task CompactControlsSwitchModesWithoutClearingTheGraph() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var connect = new TaskCompletionSource<HardwareCalibrationSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new DashboardEngine();
        engine.Begin(new("live-device-axis"));
        for (var i = 0; i < 20; i++) { engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(i * 10), 1, new(1000, i * 10000, 2000))); }
        await using var main = new MainWindow(engine, (_, _, _) => connect.Task,
            new SettingsAndTrayTests.FakeDesktop().Services);
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        model.Refresh();
        var content = Assert.IsAssignableFrom<FrameworkElement>(main.Content);
        content.Measure(new Size(1288, 862)); content.Arrange(new Rect(0, 0, 1288, 862)); content.UpdateLayout();
        var plot = Assert.IsType<SherpaAxis.App.Controls.SignalPlot>(main.FindName("MainSignalPlot"));
        Assert.True(plot.ActualHeight >= 250);
        var combo = Assert.IsAssignableFrom<ComboBox>(main.FindName("BaselineModeSelector"));
        var bind = Assert.IsType<Button>(main.FindName("BindCorrectionButton"));
        Assert.False(bind.IsVisible);
        combo.SelectedItem = "By button";
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.True(model.IsButtonCorrection);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)bind.Parent).Visibility);
        combo.SelectedItem = "Auto";
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.False(model.IsButtonCorrection);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)bind.Parent).Visibility);
        model.Refresh(); content.UpdateLayout();
        var before = Pixels(plot);
        var pending = main.RequestPanelModeAsync(SidePanelMode.Calibration);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        content.UpdateLayout();
        Assert.True(model.IsSwitchingMode);
        Assert.Equal(before, Pixels(plot));
        Assert.Equal(1, combo.Opacity);
        Assert.Equal(1, Assert.IsType<Border>(combo.Template.FindName("ComboSurface", combo)).Opacity);
        connect.SetException(new IOException("Simulated offline controller"));
        await pending;
        await main.RequestPanelModeAsync(SidePanelMode.Settings);
        var texts = Descendants(content).OfType<TextBlock>().Select(text => text.Text).ToArray();
        Assert.DoesNotContain("Tray controls", texts);
        Assert.DoesNotContain("Start live", Descendants(content).OfType<Button>().Select(button => button.Content as string));
        Assert.DoesNotContain("Profile tools", Descendants(content).OfType<Expander>().Select(expander => expander.Header as string));
    });

    private static byte[] Pixels(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }

    [Fact]
    public Task AutoLoadIsExclusiveAndRestartIgnoresTheLastManuallySelectedProfile() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-autoload-" + Guid.NewGuid().ToString("N"));
        var library = new ProfileLibrary(directory);
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        try
        {
            var firstPath = await library.SaveAsync(new("First", DashboardEngine.DefaultProfile().Axes));
            var secondPath = await library.SaveAsync(new("Second", DashboardEngine.DefaultProfile().Axes));
            await library.SetAutoLoadAsync(firstPath);
            await using (var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services, profileLibrary: library))
            {
                await main.InitializeForLaunchAsync();
                var model = Assert.IsType<DashboardViewModel>(main.DataContext);
                Assert.Equal("First", model.SelectedProfile!.Name);
                var second = model.ProfileTabs.Single(tab => tab.Name == "Second");
                model.SwitchProfile(second);
                Assert.False(model.SelectedAutoLoad);
                await main.SetSelectedAutoLoadAsync(true);
                Assert.Same(second, Assert.Single(model.ProfileTabs, tab => tab.AutoLoad));
                Assert.Equal(secondPath, await library.AutoLoadPathAsync());
                model.SwitchProfile(model.ProfileTabs.Single(tab => tab.Name == "First"));
            }
            await using (var restarted = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services, profileLibrary: library))
            {
                await restarted.InitializeForLaunchAsync();
                Assert.Equal("Second", Assert.IsType<DashboardViewModel>(restarted.DataContext).SelectedProfile!.Name);
                await restarted.SetSelectedAutoLoadAsync(false);
                Assert.Null(await library.AutoLoadPathAsync());
            }
            await using var noAutoLoad = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services, profileLibrary: library);
            await noAutoLoad.InitializeForLaunchAsync();
            Assert.Equal(DashboardEngine.DefaultProfile().Name, Assert.IsType<DashboardViewModel>(noAutoLoad.DataContext).SelectedProfile!.Name);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });

    [Fact]
    public void AutoLearnsOnlyOneStableBoundedReleaseAndResetsOnGapsOrReconnect()
    {
        var learner = new AutomaticBaseline();
        var calibration = new AxisCalibration(0, 1000);
        for (var i = 0; i < 200; i++) { Assert.Null(learner.Observe(3, i * .01, 1, calibration, 5, .05)); }
        Assert.Null(learner.Observe(500, 2, 1, calibration, 5, .05));
        for (var i = 1; i <= 110; i++) { learner.Observe(3, 2 + i * .01, 1, calibration, 5, .05); }
        Assert.True(learner.Learning);
        Assert.Equal(3, learner.Observe(3, 3.11, 1, calibration, 5, .05));
        learner.Complete();
        Assert.Null(learner.Observe(4, 3.12, 1, calibration, 5, .05));
        learner.Observe(500, 3.13, 1, calibration, 5, .05);
        Assert.Null(learner.Observe(3, 4, 1, calibration, 5, .05));
        learner.Observe(500, 4.01, 1, calibration, 5, .05);
        Assert.Null(learner.Observe(3, 4.02, 2, calibration, 5, .05));
        learner.Observe(500, 4.03, 2, calibration, 5, .05);
        for (var i = 1; i <= 200; i++) { Assert.Null(learner.Observe(i % 2 == 0 ? 1 : 4, 4.03 + i * .01, 2, calibration, 5, .05)); }
    }

    [Theory]
    [InlineData("keyboard", 65, null)]
    [InlineData("controller", 48, "wheel-device")]
    public void BoundControlMustBeReleasedFirstAndOnlyAdjustsItsPedalWhileHeld(string kind, int code, string? device)
    {
        using var buttons = new ButtonStateBuffer();
        var button = new CorrectionButton(kind, code, device);
        var engine = new DashboardEngine();
        var axes = Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, axis => new AxisProfile(new(0, 1000),
            maximumBaselineCorrection: 5, correctionMode: axis == PedalAxis.Brake ? BaselineCorrectionMode.ByButton : BaselineCorrectionMode.Off,
            correctionButton: axis == PedalAxis.Brake ? button : null));
        engine.Configure(new("Bound", axes), axes.Keys.ToDictionary(axis => axis, _ => new AxisSessionSettings(100, .05)));
        engine.Begin(new("live-device-axis"));
        void Sample(int i) => engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(i * 10), 1, new(3, 3, 3)));
        buttons.Set(button, true);
        Sample(0); engine.ProcessCorrectionButtons(buttons); Sample(1);
        Assert.Equal(0, engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result!.Value.Processing.BaselineCorrection);
        buttons.Set(button, false); engine.ProcessCorrectionButtons(buttons);
        buttons.Set(button, true);
        for (var i = 2; i < 22; i++) { engine.ProcessCorrectionButtons(buttons); Sample(i); }
        var frame = engine.Snapshot(PedalAxis.Brake);
        var correction = frame.Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result!.Value.Processing.BaselineCorrection;
        Assert.InRange(correction, .01, 3);
        Assert.All(frame.Axes.Where(axis => axis.Axis != PedalAxis.Brake), axis => Assert.Equal(0, axis.Result!.Value.Processing.BaselineCorrection));
        buttons.Set(button, false); engine.ProcessCorrectionButtons(buttons); Sample(22);
        Assert.Equal(correction, engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result!.Value.Processing.BaselineCorrection);
    }

    [Fact]
    public void ButtonStatesDeduplicateAndDeviceRemovalDoesNotReleaseOtherControls()
    {
        using var buttons = new ButtonStateBuffer();
        var wheel = new CorrectionButton("controller", 64, "wheel");
        var other = new CorrectionButton("controller", 64, "gamepad");
        var key = new CorrectionButton("keyboard", 65);
        var presses = 0;
        buttons.Pressed += _ => presses++;
        buttons.Set(wheel, true); buttons.Set(wheel, true); buttons.Set(other, true); buttons.Set(key, true);
        Assert.Equal(3, presses);
        Assert.True(buttons.IsDown(new("controller", 64, "WHEEL")));
        buttons.RemoveDevice("wheel");
        Assert.False(buttons.IsDown(wheel)); Assert.True(buttons.IsDown(other)); Assert.True(buttons.IsDown(key));
    }

    [Fact]
    public Task WindowsButtonInputRegistersAndDisposesAgainstAnOwnedHiddenWindow() => HardwarePedalUiTests.OnUiAsync(() =>
    {
        using var window = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("SherpaAxis input test")
        { Width = 1, Height = 1, WindowStyle = 0 });
        using var input = new WindowsButtonInput(window);
        Assert.False(input.IsDown(new("controller", 64, "test-unconnected-device")));
        input.Dispose();
        input.Dispose();
        return Task.CompletedTask;
    });

    [Fact]
    public void AutomaticCorrectionReachesStableReleasedBaselineAndClearsAfterReconnect()
    {
        var engine = new DashboardEngine();
        var axes = Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, axis => new AxisProfile(new(0, 1000),
            maximumBaselineCorrection: 5, correctionMode: axis == PedalAxis.Brake ? BaselineCorrectionMode.Auto : BaselineCorrectionMode.Off));
        engine.Configure(new("Automatic", axes), axes.Keys.ToDictionary(axis => axis, _ => new AxisSessionSettings(100, .05)));
        engine.Begin(new("live-device-axis"));
        for (var i = 0; i <= 250; i++)
        {
            var brake = i < 10 ? 500 : 3;
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(i * 10), 1, new(3, brake, 3)));
        }
        var before = engine.Snapshot(PedalAxis.Brake);
        Assert.Equal(3, before.Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result!.Value.Processing.BaselineCorrection);
        Assert.All(before.Axes.Where(axis => axis.Axis != PedalAxis.Brake), axis => Assert.Equal(0, axis.Result!.Value.Processing.BaselineCorrection));
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(2510), 2, new(3, 3, 3)));
        Assert.Equal(0, engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result!.Value.Processing.BaselineCorrection);
    }

    [Fact]
    public async Task SimultaneousSourceStopsShareCleanupAndCanBeRestarted()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        async IAsyncEnumerable<PedalMonitorEvent> HeldInput([EnumeratorCancellation] CancellationToken token)
        {
            Interlocked.Increment(ref runs); entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cleanup.TrySetResult(); await release.Task; }
            yield break;
        }
        await using var source = new DashboardSource(new DashboardEngine());
        await source.StartAsync(HeldInput);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var first = source.StopAsync();
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = source.StopAsync();
        Assert.Same(first, second);
        release.SetResult(); await Task.WhenAll(first, second);
        Assert.False(source.IsRunning);
        await source.StartAsync(Input);
        Assert.True(source.IsRunning); Assert.Equal(1, runs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DriverFailuresAreRetriedWithoutStoppingLiveInputOrHammeringTheDriver(bool failOnWrite)
    {
        var engine = new DashboardEngine();
        await using var source = new DashboardSource(engine);
        using var buttons = new ButtonStateBuffer();
        var attempts = 0;
        await using var runtime = new PedalRuntime(engine, source, new(Input, () =>
        {
            Interlocked.Increment(ref attempts);
            if (failOnWrite) { return new Controller { FailNonzero = true }; }
            throw new IOException("Test driver unavailable");
        }), buttons);
        await runtime.ResumeAsync();
        await WaitUntil(() => Volatile.Read(ref attempts) == 1 && runtime.Status.Contains("unavailable", StringComparison.Ordinal));
        await Task.Delay(80);
        Assert.Equal(1, Volatile.Read(ref attempts)); Assert.True(source.IsRunning);
        await WaitUntil(() => Volatile.Read(ref attempts) >= 2);
        Assert.True(engine.CanPublishOutput);
    }

    [Fact]
    public async Task RuntimeStartsAutomaticallyRearmsAfterSettingsAndStopsForCalibration()
    {
        var engine = new DashboardEngine();
        await using var source = new DashboardSource(engine);
        using var buttons = new ButtonStateBuffer();
        var controllers = new ConcurrentQueue<Controller>();
        await using var runtime = new PedalRuntime(engine, source, new(Input, () => { var controller = new Controller(); controllers.Enqueue(controller); return controller; }), buttons);
        await runtime.ResumeAsync();
        await WaitUntil(() => controllers.Any(controller => controller.Values.Any(value => value.Throttle > 0)));
        var first = controllers.First();
        engine.Configure(DashboardEngine.DefaultProfile(), Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, _ => new AxisSessionSettings(100, .05)));
        await WaitUntil(() => first.Closed && controllers.Count >= 2 && controllers.Last().Values.Any(value => value.Throttle > 0));
        await runtime.PauseAsync();
        Assert.True(runtime.IsPaused); Assert.False(source.IsRunning);
        Assert.All(controllers, controller => { Assert.True(controller.Closed); Assert.Equal(default, controller.Values.Last()); });
        await runtime.ResumeAsync();
        await WaitUntil(() => controllers.Count >= 3 && controllers.Last().Values.Any(value => value.Throttle > 0));
    }

    private static async IAsyncEnumerable<PedalMonitorEvent> Input([EnumeratorCancellation] CancellationToken token)
    {
        for (var i = 0; ; i++)
        {
            token.ThrowIfCancellationRequested();
            yield return new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(i * 5), 1, new(4000, 1000, 1000));
            await Task.Delay(5, token);
        }
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(10, deadline.Token); }
    }
    private sealed class Controller : IVirtualController
    {
        public ConcurrentQueue<PedalOutput> Values { get; } = new();
        public bool FailNonzero { get; init; }
        private int closed;
        public bool Closed => Volatile.Read(ref closed) != 0;
        public void Write(PedalOutput output)
        {
            if (FailNonzero && output != default) { throw new IOException("Test driver unavailable"); }
            Values.Enqueue(output);
        }
        public void Dispose() => Volatile.Write(ref closed, 1);
    }
}
