using System.IO;
using System.Runtime.CompilerServices;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Recording;
using SherpaAxis.App.Services.Runtime;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class PlaybackTests
{
    private const string Header = "elapsed_seconds,connection,throttle_device_axis,brake_device_axis,clutch_device_axis\n";

    [Fact]
    public async Task PlaybackPreservesEveryRowTimestampAndConnectionIncludingDuplicateTimes()
    {
        var path = Temporary(Header + "0,1,0,100,0\n0,1,0,110,0\n0.002,2,0,120,0\n");
        try
        {
            var samples = new List<PedalMonitorEvent>();
            await foreach (var item in CapturePlayback.ReadAsync(path, 20))
            {
                if (item.Axes.HasValue)
                {
                    samples.Add(item);
                }
            }

            Assert.Equal(3, samples.Count);
            Assert.Equal([100, 110, 120], samples.Select(sample => sample.Axes!.Value.Brake));
            Assert.Equal(TimeSpan.Zero, samples[1].Elapsed);
            Assert.Equal(TimeSpan.FromSeconds(.002), samples[2].Elapsed);
            Assert.Equal(2, samples[2].ConnectionNumber);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ShutdownCancellationStopsInputAndPreventsStartingAnotherSource()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var engine = new DashboardEngine();
        await using var source = new DashboardSource(engine);
        using var lifetime = new CancellationTokenSource();
        await source.StartAsync(Input, cancellationToken: lifetime.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await lifetime.CancelAsync();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await source.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.StartAsync(Input, cancellationToken: lifetime.Token));
        Assert.Equal(1, starts);
        Assert.False(source.IsRunning);
        Assert.All(engine.Snapshot(PedalAxis.Brake).Axes, axis => Assert.False(axis.IsFresh));

        async IAsyncEnumerable<PedalMonitorEvent> Input([EnumeratorCancellation] CancellationToken token)
        {
            starts++;
            try
            {
                yield return new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, 100, 0));
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally { stopped.TrySetResult(); }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("elapsed,input\n0,1\n")]
    [InlineData(Header)]
    [InlineData(Header + "0,0,0,100,0\n")]
    [InlineData(Header + "0,1,0,16777217,0\n")]
    [InlineData(Header + "0.01,1,0,100,0\n0,1,0,100,0\n")]
    [InlineData(Header + "0,2,0,100,0\n0,1,0,100,0\n")]
    [InlineData(Header + "0,1,0,-1,0\n")]
    [InlineData(Header + "0,1,0,100\n")]
    public async Task MalformedCapturesFailExplicitly(string content)
    {
        var path = Temporary(content);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await foreach (var _ in CapturePlayback.ReadAsync(path, 100)) { }
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CancellingDuringALongRecordedGapEndsPromptly()
    {
        var path = Temporary(Header + "0,1,0,100,0\n100,1,0,110,0\n");
        using var cancellation = new CancellationTokenSource();
        try
        {
            await using var reader = CapturePlayback.ReadAsync(path, 1, cancellation.Token).GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            Assert.True(await reader.MoveNextAsync());
            var waiting = reader.MoveNextAsync().AsTask();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StoppingTheSourceCancelsAcquisitionAndMarksTheLastFrameStale()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new DashboardEngine();
        await using var source = new DashboardSource(engine);
        await source.StartAsync(Events);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(source.IsRunning);
        await source.StopAsync();
        Assert.False(source.IsRunning);
        var frame = engine.Snapshot(PedalAxis.Brake);
        Assert.Equal("Stopped", frame.Detail);
        Assert.False(frame.Axes.Single(axis => axis.Axis == PedalAxis.Brake).IsFresh);

        async IAsyncEnumerable<PedalMonitorEvent> Events([EnumeratorCancellation] CancellationToken token)
        {
            yield return new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, 100, 0));
            arrived.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceErrorsRemainVisibleAndAllowASubsequentRestart(bool malformed)
    {
        var engine = new DashboardEngine();
        await using var source = new DashboardSource(engine);
        await source.StartAsync(Failure);
        await source.StopAsync();
        Assert.Contains("Source error: test failure", engine.Snapshot(PedalAxis.Brake).Detail, StringComparison.Ordinal);
        await source.StartAsync(Failure);
        await source.StopAsync();
        Assert.False(source.IsRunning);

        async IAsyncEnumerable<PedalMonitorEvent> Failure([EnumeratorCancellation] CancellationToken _)
        {
            await Task.CompletedTask;
            yield return new(PedalConnectionState.Connected, TimeSpan.Zero, 1);
            throw malformed ? new InvalidDataException("test failure") : new IOException("test failure");
        }
    }

    private static string Temporary(string content)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"playback-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        return path;
    }
}
