using System.Collections.Concurrent;
using System.IO;
using SherpaAxis.App.Models;
using SherpaAxis.Bridge;
using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class BridgeTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(.5, 16384)]
    [InlineData(1, 32767)]
    public void MappingUsesTheActualLogicalRange(double value, int expected) => Assert.Equal(expected, VJoyController.Map(value, 1, 32767));

    [Fact]
    public void InvalidValuesCannotReachNativeOutput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VJoyController.Map(double.NaN, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => VJoyController.Map(1.01, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => VJoyController.Map(0, 100, 100));
        Assert.Throws<ArgumentException>(() => new VJoyController("relative.dll"));
        Assert.Throws<FileNotFoundException>(() => new VJoyController(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll")));
    }

    [Fact]
    public void ReplayNeverFeedsVirtualOutputAndLiveMappingUsesProcessedAxes()
    {
        var engine = new DashboardEngine();
        var sink = new Sink();
        engine.Begin(new("capture-replay"));
        engine.Accept(Sample(0));
        Assert.Throws<InvalidOperationException>(() => engine.AttachOutput(sink));
        engine.Begin(new("live-device-axis"));
        engine.Accept(Sample(0));
        engine.AttachOutput(sink);
        Assert.Equal(new PedalOutput(.25, .5, .75), sink.Last);
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.002), 1, new(16384, 16777216, 16384)));
        Assert.Equal(new PedalOutput(1, 1, 1), sink.Last);
        engine.End("Stopped");
        Assert.True(sink.Invalidated);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("connection")]
    [InlineData("gap")]
    public void ProcessingBoundariesDisarmOutput(string boundary)
    {
        var engine = new DashboardEngine();
        var sink = new Sink();
        engine.Begin(new("live-device-axis"));
        engine.Accept(Sample(0));
        engine.AttachOutput(sink);
        if (boundary == "configuration")
        {
            engine.Configure(DashboardEngine.DefaultProfile(), Enum.GetValues<PedalAxis>().ToDictionary(a => a, _ => new AxisSessionSettings(1, 1)));
        }
        else { engine.Accept(Sample(boundary == "gap" ? .2 : .01, boundary == "connection" ? 2 : 1)); }
        Assert.True(sink.Invalidated);
    }

    [Fact]
    public async Task BackgroundOutputIsNeutralizedAndReleasedWhenInputBecomesStale()
    {
        var controller = new Controller();
        var clock = new ManualClock();
        await using var bridge = new OutputBridge(() => controller, clock);
        await bridge.Ready;
        bridge.Submit(new(.2, .3, .4));
        await controller.Nonzero.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Ticks = 101;
        await controller.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(default, controller.Values.Last());
        Assert.Contains("no fresh input", bridge.Status, StringComparison.Ordinal);
        bridge.Submit(new(1, 1, 1));
        Assert.Equal(default, controller.Values.Last());
    }

    [Fact]
    public async Task WriteFailureDoesNotEscapeIntoAcquisitionAndAlwaysReleasesDevice()
    {
        var controller = new Controller { FailNonzero = true };
        await using var bridge = new OutputBridge(() => controller);
        await bridge.Ready;
        bridge.Submit(new(1, 1, 1));
        await controller.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("simulated driver failure", bridge.Status, StringComparison.Ordinal);
        Assert.Equal(default, controller.Values.Last());
    }

    [Fact]
    public async Task AcquisitionNeverWaitsForSlowDriverAndExplicitStopWritesZero()
    {
        var controller = new Controller { BlockNonzero = true };
        var bridge = new OutputBridge(() => controller);
        await bridge.Ready;
        bridge.Submit(new(1, 1, 1));
        await controller.Nonzero.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 20000; i++) { bridge.Submit(new(.5, .5, .5)); }
        bridge.Invalidate("test stopped");
        controller.Unblock.SetResult();
        await bridge.DisposeAsync();
        Assert.True(controller.Closed.Task.IsCompleted);
        Assert.Equal(default, controller.Values.Last());
    }

    private static PedalMonitorEvent Sample(double seconds, int connection = 1) =>
        new(PedalConnectionState.Connected, TimeSpan.FromSeconds(seconds), connection, new(4096, 8388608, 12288));

    private sealed class Sink : IProcessedOutputSink
    {
        public PedalOutput Last { get; private set; }
        public bool Invalidated { get; private set; }
        public void Submit(PedalOutput output) { Assert.False(Invalidated); Last = output; }
        public void Invalidate(string reason) => Invalidated = true;
    }
    private sealed class Controller : IVirtualController
    {
        public ConcurrentQueue<PedalOutput> Values { get; } = new();
        public bool FailNonzero { get; init; }
        public bool BlockNonzero { get; init; }
        public TaskCompletionSource Nonzero { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Unblock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Write(PedalOutput output)
        {
            if (output != default)
            {
                Nonzero.TrySetResult();
                if (FailNonzero) { throw new IOException("simulated driver failure"); }
                if (BlockNonzero) { Unblock.Task.GetAwaiter().GetResult(); }
            }
            Values.Enqueue(output);
        }
        public void Dispose() => Closed.TrySetResult();
    }
    private sealed class ManualClock : TimeProvider
    {
        public long Ticks { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
    }
}
