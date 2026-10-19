using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.App.Services.Recording;
using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class RecordingTests
{
    [Fact]
    public async Task RecordingRetainsEveryReportAndSettingsWhileTheDisplayIsDecimated()
    {
        using var input = new StringWriter(CultureInfo.InvariantCulture);
        using var processed = new StringWriter(CultureInfo.InvariantCulture);
        using var events = new StringWriter(CultureInfo.InvariantCulture);
        await using var recorder = new RecordingSession(input, processed, events);
        var engine = new DashboardEngine();
        engine.Begin(new("capture-replay", "test.csv", 20));
        engine.AttachRecording(recorder);
        for (var index = 0; index < 1000; index++)
        {
            engine.Accept(Sample(index * .002, index));
        }

        engine.DetachRecording();
        var status = await recorder.StopAsync();
        Assert.Null(status.Failure);
        Assert.Equal(1000, status.AcceptedSamples);
        Assert.Equal(1000, status.WrittenSamples);
        Assert.Equal(1001, Lines(input).Length);
        Assert.Equal(1001, Lines(processed).Length);
        Assert.True(engine.Snapshot(PedalAxis.Brake).History.Count < 1000);
        var raw = Lines(input);
        var rows = Csv(processed);
        for (var index = 0; index < 1000; index++)
        {
            Assert.Equal(index.ToString(CultureInfo.InvariantCulture), raw[index + 1].Split(',')[3]);
            Assert.Equal(index.ToString(CultureInfo.InvariantCulture), rows[index]["brake_input"]);
            Assert.Equal(index / 16777216d, double.Parse(rows[index]["brake_output"], CultureInfo.InvariantCulture));
        }

        var metadata = Json(events);
        var start = metadata.First().GetProperty("details");
        Assert.Equal("capture-replay", start.GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal(20, start.GetProperty("source").GetProperty("playbackSpeed").GetDouble());
        Assert.Equal(ProfileJson.SchemaVersion, start.GetProperty("configuration").GetProperty("profile").GetProperty("schemaVersion").GetInt32());
        Assert.Equal(3, start.GetProperty("configuration").GetProperty("sessionLimits").GetArrayLength());
        Assert.Null(start.GetProperty("hardwareSnapshot").Deserialize<object>());
        Assert.True(metadata.Last().GetProperty("complete").GetBoolean());
        Assert.Equal(1000, metadata.Last().GetProperty("writtenSamples").GetInt64());
    }

    [Fact]
    public async Task ConfigurationChangesRequestsAndResetsAreOrderedWithTheirProcessedRows()
    {
        using var input = new StringWriter(CultureInfo.InvariantCulture);
        using var processed = new StringWriter(CultureInfo.InvariantCulture);
        using var events = new StringWriter(CultureInfo.InvariantCulture);
        await using var recorder = new RecordingSession(input, processed, events);
        var engine = new DashboardEngine();
        var model = new SherpaAxis.App.ViewModels.DashboardViewModel(engine);
        engine.Begin(new("live-device-axis"));
        engine.AttachRecording(recorder);
        engine.Accept(Sample(0, 95));
        model.Editor.Minimum = "100";
        model.Editor.Maximum = "200";
        model.Editor.MaximumCorrection = "10";
        model.Editor.FilterMilliseconds = "10";
        model.Apply();
        engine.Accept(Sample(.01, 95));
        engine.ConfirmBaseline(PedalAxis.Brake);
        engine.AuthorizeRelease(PedalAxis.Brake);
        engine.Accept(Sample(.02, 95));
        engine.RevokeRelease();
        engine.DisableCompensation(PedalAxis.Brake);
        engine.Accept(Sample(.03, 110));
        engine.Accept(Sample(.2, 110));
        engine.Accept(new(PedalConnectionState.Disconnected, TimeSpan.FromSeconds(.21), 1));
        engine.Accept(Sample(.3, 120, 2));
        engine.End("Stopped");
        var status = await recorder.StopAsync();
        Assert.Null(status.Failure);
        var metadata = Json(events);
        var configuration = metadata.Single(item => Kind(item) == "configuration-applied");
        var rows = Csv(processed);
        Assert.NotEqual(rows[0]["configuration_revision"], rows[1]["configuration_revision"]);
        Assert.Equal(configuration.GetProperty("configurationRevision").GetInt64().ToString(CultureInfo.InvariantCulture), rows[1]["configuration_revision"]);
        Assert.True(long.Parse(rows[1]["sequence"], CultureInfo.InvariantCulture) > configuration.GetProperty("sequence").GetInt64());
        Assert.Contains(metadata, item => Kind(item) == "baseline-requested");
        Assert.Contains(metadata, item => Kind(item) == "compensation-disable-requested");
        Assert.Contains(metadata, item => Kind(item) == "release-permission-changed");
        Assert.Contains(metadata, item => Kind(item) == "release-control-revoked");
        Assert.Contains(metadata, item => Kind(item) == "connection-state");
        Assert.Contains(metadata, item => Kind(item) == "session-reset" && item.GetProperty("details").GetProperty("brake").GetString() == "samplingGap");
        Assert.Equal("source-ended", Kind(metadata[^2]));
        Assert.Equal(.3, metadata[^2].GetProperty("elapsedSeconds").GetDouble());
        Assert.Equal("0", rows[^1]["brake_baseline_correction"]);
    }

    [Fact]
    public async Task StartingMidSessionPreservesRuntimeStateAndExcludesLocalDevicePaths()
    {
        using var input = new StringWriter(CultureInfo.InvariantCulture);
        using var processed = new StringWriter(CultureInfo.InvariantCulture);
        using var events = new StringWriter(CultureInfo.InvariantCulture);
        await using var recorder = new RecordingSession(input, processed, events);
        var engine = new DashboardEngine();
        engine.Configure(new("Test", new Dictionary<PedalAxis, AxisProfile> { [PedalAxis.Brake] = new(new(100, 200), maximumBaselineCorrection: 10) }),
            Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, _ => new AxisSessionSettings(10, 1)));
        engine.Begin(new("live-device-axis"));
        engine.Accept(Sample(0, 95));
        engine.ConfirmBaseline(PedalAxis.Brake);
        var before = engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result;
        var source = new SherpaAxis.Devices.HidDeviceInfo("private-device-path", 0x0483, 0xA2EA, 0x200, 0xFF00, 1, 0, 0, 17);
        engine.RecordHardware(new(1, DateTimeOffset.UtcNow, source, new(new(1, 2), new(3, 4), new(5, 6))));
        engine.AttachRecording(recorder);
        Assert.Equal(before, engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result);
        engine.Accept(Sample(.01, 95));
        engine.DetachRecording();
        await recorder.StopAsync();
        var details = Json(events)[0].GetProperty("details");
        Assert.Equal(-5, details.GetProperty("runtimeSeed").GetProperty("brake").GetProperty("targetCorrection").GetDouble());
        Assert.Equal("currentlyConnectedDevice", details.GetProperty("hardwareSnapshot").GetProperty("scope").GetString());
        Assert.DoesNotContain("private-device-path", events.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowStorageFailsRecordingWithoutBlockingOrDroppingPreviewProcessing()
    {
        using var input = new GatedWriter();
        using var processed = new StringWriter(CultureInfo.InvariantCulture);
        using var events = new StringWriter(CultureInfo.InvariantCulture);
        await using var recorder = new RecordingSession(input, processed, events, queueCapacity: 4);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var engine = new DashboardEngine();
        engine.Begin();
        engine.AttachRecording(recorder);
        for (var index = 0; index < 100; index++)
        {
            engine.Accept(Sample(index * .002, index));
        }

        Assert.Equal(100, engine.Snapshot(PedalAxis.Brake).SampleCount);
        engine.DetachRecording();
        var status = await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<IOException>(status.Failure);
        Assert.Contains("buffer exhausted", status.Failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("recording-completed", events.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopHasADrainDeadlineAndNeverReportsAnUnflushedCaptureAsComplete()
    {
        using var input = new GatedWriter();
        using var processed = new StringWriter(CultureInfo.InvariantCulture);
        using var events = new StringWriter(CultureInfo.InvariantCulture);
        await using var recorder = new RecordingSession(input, processed, events, drainTimeout: TimeSpan.FromMilliseconds(30));
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var status = await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<TimeoutException>(status.Failure);
        Assert.DoesNotContain("recording-completed", events.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFlushFailurePreservesTheFirstErrorAndOmitsTheCompletionMarker()
    {
        using var input = new FlushFailureWriter();
        using var processed = new StringWriter(CultureInfo.InvariantCulture);
        using var events = new StringWriter(CultureInfo.InvariantCulture);
        await using var recorder = new RecordingSession(input, processed, events);
        var status = await recorder.StopAsync();
        Assert.IsType<IOException>(status.Failure);
        Assert.Equal("flush failed", status.Failure!.Message);
        Assert.DoesNotContain("recording-completed", events.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".csv")]
    [InlineData(".processed.csv")]
    [InlineData(".session.jsonl")]
    public async Task ExistingFilesAreUntouchedAndNewEmptyFilesAreRolledBack(string suffix)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, $"recording-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "pedals" + suffix);
        File.WriteAllText(path, "keep this");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => RecordingSession.CreateAsync(Path.Combine(folder, "pedals.csv")));
            Assert.Equal("keep this", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task FileRecordingClosesItsStreamsAndItsInputCanBeReplayed()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"recording-{Guid.NewGuid():N}.csv");
        var processed = Path.ChangeExtension(path, ".processed.csv");
        var events = Path.ChangeExtension(path, ".session.jsonl");
        try
        {
            await using var recorder = await RecordingSession.CreateAsync(path);
            var engine = new DashboardEngine();
            engine.Begin();
            engine.AttachRecording(recorder);
            engine.Accept(Sample(.0000001, 95));
            engine.Accept(Sample(.0020001, 105));
            engine.End("Playback complete");
            var status = await recorder.StopAsync();
            Assert.Null(status.Failure);
            var samples = new List<PedalMonitorEvent>();
            await foreach (var sample in CapturePlayback.ReadAsync(path, 100))
            {
                if (sample.Axes.HasValue) { samples.Add(sample); }
            }

            Assert.Equal(2, samples.Count);
            Assert.Equal(TimeSpan.FromTicks(1), samples[0].Elapsed);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            File.Delete(path);
            File.Delete(processed);
            File.Delete(events);
        }
    }

    [Fact]
    public async Task SourceErrorsInvalidateRecordingCompletion()
    {
        using var input = new StringWriter(CultureInfo.InvariantCulture);
        using var processed = new StringWriter(CultureInfo.InvariantCulture);
        using var events = new StringWriter(CultureInfo.InvariantCulture);
        await using var recorder = new RecordingSession(input, processed, events);
        var engine = new DashboardEngine();
        engine.Begin();
        engine.AttachRecording(recorder);
        engine.Accept(Sample(0, 95));
        engine.End("Source error: malformed capture");
        var status = await recorder.StopAsync();
        Assert.IsType<IOException>(status.Failure);
        Assert.DoesNotContain("recording-completed", events.ToString(), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => engine.AttachRecording(recorder));
    }

    [Fact]
    public async Task FinishingRecordingLeavesTheLiveProcessingSessionUnchanged()
    {
        using var input = new StringWriter(CultureInfo.InvariantCulture);
        using var processed = new StringWriter(CultureInfo.InvariantCulture);
        using var events = new StringWriter(CultureInfo.InvariantCulture);
        await using var recorder = new RecordingSession(input, processed, events);
        var engine = new DashboardEngine();
        var model = new SherpaAxis.App.ViewModels.DashboardViewModel(engine);
        model.Editor.Minimum = "100";
        model.Editor.Maximum = "200";
        model.Editor.FilterMilliseconds = "10";
        model.Apply();
        engine.Begin();
        engine.Accept(Sample(0, 100));
        engine.AttachRecording(recorder);
        engine.Accept(Sample(.01, 200));
        var before = engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result;
        engine.DetachRecording();
        await recorder.StopAsync();
        Assert.Equal(before, engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result);
        engine.Accept(Sample(.02, 200));
        var after = engine.Snapshot(PedalAxis.Brake).Axes.Single(axis => axis.Axis == PedalAxis.Brake).Result!.Value;
        Assert.Equal(AxisSessionResetReason.None, after.ResetReason);
        Assert.False(after.Processing.FilterReset);
        Assert.Equal(1 - Math.Exp(-2), after.Processing.Output, 12);
        Assert.Equal(2, Lines(input).Length);
    }

    [Fact]
    public async Task ReconnectInvalidatesThePreviousHardwareSnapshot()
    {
        using var input = new StringWriter(CultureInfo.InvariantCulture);
        using var processed = new StringWriter(CultureInfo.InvariantCulture);
        using var events = new StringWriter(CultureInfo.InvariantCulture);
        await using var recorder = new RecordingSession(input, processed, events);
        var engine = new DashboardEngine();
        engine.Begin();
        engine.Accept(Sample(0, 95));
        var device = new SherpaAxis.Devices.HidDeviceInfo("private", 0x0483, 0xA2EA, 0x200, 0xFF00, 1, 0, 0, 17);
        engine.RecordHardware(new(1, DateTimeOffset.UtcNow, device, new(new(1, 2), new(3, 4), new(5, 6))));
        engine.Accept(new(PedalConnectionState.Disconnected, TimeSpan.FromSeconds(.01), 1));
        engine.Accept(Sample(.02, 95, 2));
        engine.AttachRecording(recorder);
        engine.DetachRecording();
        await recorder.StopAsync();
        Assert.Equal(JsonValueKind.Null, Json(events)[0].GetProperty("details").GetProperty("hardwareSnapshot").ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailureInAnyCompanionWriterPreventsSuccess(int destination)
    {
        using var input = new StringWriter(CultureInfo.InvariantCulture);
        using var processed = new StringWriter(CultureInfo.InvariantCulture);
        using var events = new StringWriter(CultureInfo.InvariantCulture);
        using var broken = new FailingWriter();
        await using var recorder = new RecordingSession(destination == 0 ? broken : input,
            destination == 1 ? broken : processed, destination == 2 ? broken : events);
        var engine = new DashboardEngine();
        engine.Begin();
        engine.AttachRecording(recorder);
        engine.Accept(Sample(0, 95));
        engine.DetachRecording();
        var status = await recorder.StopAsync();
        Assert.IsType<IOException>(status.Failure);
        Assert.Equal("write failed", status.Failure!.Message);
        Assert.DoesNotContain("recording-completed", events.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordingNumbersStayInvariantUnderACommaDecimalCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var input = new StringWriter();
            using var processed = new StringWriter();
            using var events = new StringWriter();
            await using var recorder = new RecordingSession(input, processed, events);
            var engine = new DashboardEngine();
            engine.Begin();
            engine.AttachRecording(recorder);
            engine.Accept(Sample(.125, 8388608));
            engine.DetachRecording();
            var status = await recorder.StopAsync();
            Assert.Null(status.Failure);
            Assert.StartsWith("0.1250000,1,", Lines(input)[1], StringComparison.Ordinal);
            Assert.Equal("0.5", Csv(processed)[0]["brake_output"]);
            Assert.Equal(49, Lines(processed)[1].Split(',').Length);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static PedalMonitorEvent Sample(double seconds, int brake, int connection = 1) =>
        new(PedalConnectionState.Connected, TimeSpan.FromSeconds(seconds), connection, new(100, brake, 200));
    private static string[] Lines(StringWriter writer) => writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
    private static JsonElement[] Json(StringWriter writer) => Lines(writer).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    private static string? Kind(JsonElement item) => item.GetProperty("kind").GetString();
    private static Dictionary<string, string>[] Csv(StringWriter writer)
    {
        var lines = Lines(writer);
        var header = lines[0].Split(',');
        return lines.Skip(1).Select(line => header.Zip(line.Split(',')).ToDictionary(pair => pair.First, pair => pair.Second)).ToArray();
    }

    private sealed class GatedWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FlushFailureWriter : StringWriter
    {
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new IOException("flush failed"));
    }

    private sealed class FailingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("write failed"));
    }
}
