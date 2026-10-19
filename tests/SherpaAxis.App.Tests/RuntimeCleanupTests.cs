using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.Bridge;
using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class RuntimeCleanupTests
{
    [Fact]
    public async Task AWorkerFailureStillStopsInputAndReleasesVirtualOutput()
    {
        var engine = new DashboardEngine();
        var axes = Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, _ => new AxisProfile(new(0, 1000),
            maximumBaselineCorrection: 5, correctionMode: BaselineCorrectionMode.ByButton, correctionButton: new("keyboard", 65)));
        engine.Configure(new("Cleanup", axes), axes.Keys.ToDictionary(axis => axis, _ => new AxisSessionSettings(100, .05)));
        await using var source = new DashboardSource(engine);
        using var buttons = new FailingButtons();
        var controller = new Controller();
        var inputStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<PedalMonitorEvent> Input([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                for (var i = 0; ; i++)
                {
                    yield return new(PedalConnectionState.Connected, TimeSpan.FromMilliseconds(i * 5), 1, new(400, 400, 400));
                    await Task.Delay(5, token);
                }
            }
            finally { inputStopped.TrySetResult(); }
        }
        await using var runtime = new PedalRuntime(engine, source, new(Input, () => controller), buttons);
        await runtime.ResumeAsync();
        await controller.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        buttons.Fail = true;
        await buttons.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<NotSupportedException>(() => runtime.DisposeAsync().AsTask());
        Assert.Equal("Simulated button reader failure", failure.Message);
        await inputStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(source.IsRunning);
        Assert.True(controller.Closed);
        Assert.Equal(default, controller.Values.Last());
        await runtime.DisposeAsync();
    }

    private sealed class FailingButtons : IButtonStateSource
    {
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool fail;
        public bool Fail { get => fail; set => fail = value; }
        public event Action<CorrectionButton>? Pressed { add { } remove { } }
        public bool IsDown(CorrectionButton button)
        {
            if (!Fail) { return false; }
            Failed.TrySetResult();
            throw new NotSupportedException("Simulated button reader failure");
        }
        public void Dispose() { }
    }

    private sealed class Controller : IVirtualController
    {
        public ConcurrentQueue<PedalOutput> Values { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed { get; private set; }
        public void Write(PedalOutput output) { Values.Enqueue(output); if (output != default) { Started.TrySetResult(); } }
        public void Dispose() => Closed = true;
    }
}
