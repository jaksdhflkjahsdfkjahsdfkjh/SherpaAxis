using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Cli.Tests;

public sealed class CaptureSessionTests
{
    [Fact]
    public async Task ABlockedDisplayDoesNotStopSamplesOrConnectionChangesFromReachingCsv()
    {
        using var terminal = new BlockingTerminalWriter();
        using var csv = new StringWriter(CultureInfo.InvariantCulture);
        var display = new CaptureDisplay(terminal, terminal);
        await terminal.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var source = Channel.CreateUnbounded<PedalMonitorEvent>();
        var capture = CaptureSession.RunAsync(source.Reader.ReadAllAsync(), csv, display);
        source.Writer.TryWrite(Sample(1, 1, 100));
        source.Writer.TryWrite(new(PedalConnectionState.Disconnected, TimeSpan.FromSeconds(2), 1));
        source.Writer.TryWrite(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(3), 2));
        source.Writer.TryWrite(Sample(2, 4, 200));
        source.Writer.TryComplete();
        try
        {
            var result = await capture.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(terminal.Released);
            Assert.Null(result.Error);
            Assert.Equal(2, result.AcquiredSamples);
            Assert.Equal(2, result.RecordedSamples);
            Assert.Equal(["1.000000,1,0,100,0", "4.000000,2,0,200,0"], Rows(csv).Skip(1));
            await display.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(terminal.Released);
        }
        finally
        {
            terminal.Release();
            await display.CompleteAsync();
            await display.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task UnpausingTheDisplayShowsTheLatestStateWithoutReplayingAStaleBacklog()
    {
        using var terminal = new BlockingTerminalWriter();
        var display = new CaptureDisplay(terminal, terminal);
        await terminal.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        display.Publish("old input");
        display.Publish("disconnected");
        display.Publish("latest input on connection 2");
        terminal.Release();
        await display.CompleteAsync();
        await display.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, terminal.Lines.Count);
        Assert.Equal("latest input on connection 2", terminal.Lines[1]);
    }

    [Fact]
    public async Task ASlowCsvWriterDoesNotBackpressureAcquisitionWhileTheBufferHasSpace()
    {
        using var csv = new GatedWriter();
        var display = new CaptureDisplay(TextWriter.Null, TextWriter.Null);
        var produced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = CaptureSession.RunAsync(ProduceAsync(), csv, display, queueCapacity: 8);
        try
        {
            await csv.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await produced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(csv.Released);
            csv.Release();
            var result = await capture.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(result.Error);
            Assert.Equal(4, result.RecordedSamples);
            Assert.Equal(5, csv.Lines.Count);
        }
        finally
        {
            csv.Release();
            await display.CompleteAsync();
        }

        async IAsyncEnumerable<PedalMonitorEvent> ProduceAsync()
        {
            await csv.Started.Task;
            for (var index = 0; index < 4; index++)
            {
                yield return Sample(1, index, index);
            }

            produced.TrySetResult();
        }
    }

    [Fact]
    public async Task AFullCsvBufferFailsExplicitlyAndDisposesTheAcquisitionEnumerator()
    {
        using var csv = new GatedWriter();
        var display = new CaptureDisplay(TextWriter.Null, TextWriter.Null);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = CaptureSession.RunAsync(ProduceAsync(), csv, display, queueCapacity: 2);
        try
        {
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            csv.Release();
            var result = await capture.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType<IOException>(result.Error);
            Assert.Contains("CSV buffer exhausted", result.Error.Message, StringComparison.Ordinal);
            Assert.Equal(3, result.AcquiredSamples);
            Assert.Null(result.RecordedSamples);
            Assert.Equal(3, csv.Lines.Count);
        }
        finally
        {
            csv.Release();
            await display.CompleteAsync();
        }

        async IAsyncEnumerable<PedalMonitorEvent> ProduceAsync()
        {
            await csv.Started.Task;
            try
            {
                for (var index = 0; index < 100; index++)
                {
                    yield return Sample(1, index, index);
                }
            }
            finally
            {
                disposed.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task DiskFailureCancelsAnOutstandingAcquisitionRead()
    {
        using var csv = new FailingWriter();
        var display = new CaptureDisplay(TextWriter.Null, TextWriter.Null);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await CaptureSession.RunAsync(ProduceAsync(), csv, display).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<IOException>(result.Error);
        Assert.Contains("Disk full", result.Error.Message, StringComparison.Ordinal);
        Assert.Null(result.RecordedSamples);
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await display.CompleteAsync();

        async IAsyncEnumerable<PedalMonitorEvent> ProduceAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return Sample(1, 1, 1);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                disposed.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task CancellationDrainsAcceptedSamplesBeforeReportingSuccess()
    {
        using var csv = new GatedWriter();
        using var cancellation = new CancellationTokenSource();
        var display = new CaptureDisplay(TextWriter.Null, TextWriter.Null);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = CaptureSession.RunAsync(ProduceAsync(), csv, display, cancellationToken: cancellation.Token);
        try
        {
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            csv.Release();
            var result = await capture.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(result.Error);
            Assert.Equal(3, result.AcquiredSamples);
            Assert.Equal(3, result.RecordedSamples);
            Assert.Equal(4, csv.Lines.Count);
        }
        finally
        {
            cancellation.Cancel();
            csv.Release();
            await display.CompleteAsync();
        }

        async IAsyncEnumerable<PedalMonitorEvent> ProduceAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await csv.Started.Task;
            for (var index = 0; index < 3; index++)
            {
                yield return Sample(1, index, index);
            }

            accepted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    [Fact]
    public async Task AnUnfinishedCsvWriteTimesOutInsteadOfClaimingACompleteCapture()
    {
        using var csv = new GatedWriter();
        var display = new CaptureDisplay(TextWriter.Null, TextWriter.Null);
        var result = await CaptureSession.RunAsync(ProduceAsync(), csv, display,
            drainTimeout: TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<TimeoutException>(result.Error);
        Assert.Null(result.RecordedSamples);
        Assert.Equal(1, result.AcquiredSamples);
        csv.Release();
        await display.CompleteAsync();

        async IAsyncEnumerable<PedalMonitorEvent> ProduceAsync()
        {
            await csv.Started.Task;
            yield return Sample(1, 1, 1);
        }
    }

    [Fact]
    public async Task AcquisitionFailureStillFlushesTheAcceptedPrefix()
    {
        using var csv = new StringWriter(CultureInfo.InvariantCulture);
        var display = new CaptureDisplay(TextWriter.Null, TextWriter.Null);
        var result = await CaptureSession.RunAsync(ProduceAsync(), csv, display).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal(2, Rows(csv).Length);
        Assert.Null(result.RecordedSamples);
        await display.CompleteAsync();

        static async IAsyncEnumerable<PedalMonitorEvent> ProduceAsync()
        {
            await Task.Yield();
            yield return Sample(1, 1, 1);
            throw new InvalidOperationException("Acquisition failed.");
        }
    }

    [Fact]
    public async Task TerminalFailureDoesNotFailACsvRecording()
    {
        using var terminal = new FailingWriter();
        using var csv = new StringWriter(CultureInfo.InvariantCulture);
        var display = new CaptureDisplay(terminal, terminal);
        var source = Channel.CreateUnbounded<PedalMonitorEvent>();
        source.Writer.TryWrite(Sample(1, 1, 100));
        source.Writer.TryComplete();
        var result = await CaptureSession.RunAsync(source.Reader.ReadAllAsync(), csv, display).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result.Error);
        Assert.Equal(1, result.RecordedSamples);
        await display.CompleteAsync();
        await display.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static PedalMonitorEvent Sample(int connection, int seconds, int brake) =>
        new(PedalConnectionState.Connected, TimeSpan.FromSeconds(seconds), connection, new PedalAxes(0, brake, 0));

    private static string[] Rows(StringWriter writer) => writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r')).ToArray();

    private sealed class GatedWriter : TextWriter
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Encoding Encoding => Encoding.UTF8;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released => release.Task.IsCompleted;
        public List<string> Lines { get; } = [];

        public void Release() => release.TrySetResult();

        public override Task WriteLineAsync(string? value) => WriteLineAsync(value.AsMemory(), CancellationToken.None);

        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            Lines.Add(buffer.ToString());
        }
    }

    private sealed class BlockingTerminalWriter : TextWriter
    {
        private readonly ManualResetEventSlim release = new();
        public override Encoding Encoding => Encoding.UTF8;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released => release.IsSet;
        public List<string> Lines { get; } = [];

        public void Release() => release.Set();

        public override Task WriteLineAsync(string? value)
        {
            Started.TrySetResult();
            release.Wait();
            Lines.Add(value ?? "");
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                release.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FailingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value) => Task.FromException(new IOException("Terminal closed."));

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Disk full."));
    }
}
