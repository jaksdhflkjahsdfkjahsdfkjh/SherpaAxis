using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Tests;

public sealed class PedalMonitorTests
{
    [Fact]
    public async Task RecoversAfterRemovalWithoutReplayingAStaleSample()
    {
        var first = new TestConnection(PedalInputReportTests.Frame(0, 12345, 0));
        var second = new TestConnection(PedalInputReportTests.Frame(0, 0, 0));
        var source = new TestSource(first, second);
        var monitor = Monitor(source);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<PedalMonitorEvent>();
        await foreach (var update in monitor.WatchAsync(cancellationToken: deadline.Token))
        {
            events.Add(update);
            if (events.Count == 5)
            {
                break;
            }
        }

        Assert.Equal([PedalConnectionState.Connected, PedalConnectionState.Connected,
            PedalConnectionState.Disconnected, PedalConnectionState.Connected, PedalConnectionState.Connected],
            events.Select(update => update.State));
        Assert.Equal(12345, events[1].Axes!.Value.Brake);
        Assert.Null(events[2].Axes);
        Assert.Equal(0, events[4].Axes!.Value.Brake);
        Assert.Equal(2, events[4].ConnectionNumber);
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.Elapsed <= pair.Second.Elapsed));
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task CancelsAnOutstandingReadAndDisposesTheConnection()
    {
        var connection = new TestConnection();
        var monitor = Monitor(new TestSource(connection));
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = monitor.WatchAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        var pending = enumerator.MoveNextAsync().AsTask();
        await connection.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task TimeoutProducesDisconnectAndDisposesTheConnection()
    {
        var connection = new TestConnection();
        var monitor = new PedalMonitor(new TestSource(connection), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(20));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = monitor.WatchAsync(cancellationToken: deadline.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(PedalConnectionState.Disconnected, enumerator.Current.State);
        Assert.Equal("Input report timeout.", enumerator.Current.Detail);
        Assert.Null(enumerator.Current.Axes);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task InvalidReportDisconnectsWithoutYieldingFabricatedAxes()
    {
        var connection = new TestConnection(new byte[8]);
        await using var enumerator = Monitor(new TestSource(connection)).WatchAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(PedalConnectionState.Disconnected, enumerator.Current.State);
        Assert.Null(enumerator.Current.Axes);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task WaitsForArrivalAndRecoversFromAccessFailure()
    {
        var connection = new TestConnection(PedalInputReportTests.Frame(10, 1000, 100));
        var source = new TestSource(connection) { AbsentPolls = 1, DeniedOpens = 1 };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var updates = new List<PedalMonitorEvent>();
        await foreach (var update in Monitor(source).WatchAsync(cancellationToken: deadline.Token))
        {
            updates.Add(update);
            if (update.Axes is not null)
            {
                break;
            }
        }

        Assert.Equal([PedalConnectionState.Waiting, PedalConnectionState.Waiting,
            PedalConnectionState.Connected, PedalConnectionState.Connected], updates.Select(update => update.State));
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task RefusesAmbiguousDevicesBeforeOpeningEitherOne()
    {
        var source = new TestSource { Ambiguous = true };
        await using var enumerator = Monitor(source).WatchAsync().GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());
        Assert.Equal(0, source.OpenCount);
    }

    [Fact]
    public async Task AnExplicitPathSelectsOneOfMultipleDevices()
    {
        var connection = new TestConnection(PedalInputReportTests.Frame(1, 2, 3));
        var source = new TestSource(connection) { Ambiguous = true };
        await foreach (var update in Monitor(source).WatchAsync("other-pedals"))
        {
            if (update.Axes is not null)
            {
                break;
            }
        }

        Assert.Equal("other-pedals", source.OpenedPath);
        Assert.True(connection.Disposed);
    }

    private static PedalMonitor Monitor(IInputReportSource source) =>
        new(source, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2));

    private sealed class TestSource(params TestConnection[] connections) : IInputReportSource
    {
        private readonly Queue<TestConnection> connections = new(connections);
        public bool Ambiguous { get; init; }
        public int AbsentPolls { get; set; }
        public int DeniedOpens { get; set; }
        public int OpenCount { get; private set; }
        public string? OpenedPath { get; private set; }

        public IReadOnlyList<HidDeviceInfo> Find(ushort vendorId, ushort productId)
        {
            if (AbsentPolls-- > 0)
            {
                return [];
            }

            return Ambiguous ? [PedalInputReportTests.Collection, PedalInputReportTests.Collection with { Path = "other-pedals" }]
                : [PedalInputReportTests.Collection];
        }

        public IInputReportConnection Open(HidDeviceInfo collection)
        {
            OpenCount++;
            if (DeniedOpens-- > 0)
            {
                throw new UnauthorizedAccessException("Access denied.");
            }

            OpenedPath = collection.Path;
            return connections.Dequeue();
        }
    }

    private sealed class TestConnection(params byte[][] reports) : IInputReportConnection
    {
        private readonly Queue<byte[]> reports = new(reports);
        private readonly bool reportsStartedWithData = reports.Length > 0;
        public bool Disposed { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            if (reports.TryDequeue(out var report))
            {
                report.CopyTo(buffer);
                return report.Length;
            }

            if (reportsStartedWithData)
            {
                throw new IOException("Device removed.");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
