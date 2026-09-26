using System.Text.Json;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Tests;

public sealed class CalibrationSnapshotReaderTests
{
    [Fact]
    public async Task ReadsOnlyConfigurationAndExportsAVersionedRoundTrip()
    {
        var features = new TestFeatures();
        var reader = new CalibrationSnapshotReader(new TestSource(PedalInputReportTests.Collection,
            PedalCalibrationReportTests.Collection), features);
        var snapshot = await reader.ReadAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, features.ReportId);
        Assert.Equal(PedalCalibrationReportTests.Collection, features.Collection);
        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal(TimeSpan.Zero, snapshot.CapturedAtUtc.Offset);
        Assert.Equal(new HardwareCalibrationRange(1, 2), snapshot.Calibration.Throttle);
        Assert.Equal(new HardwareCalibrationRange(3, 4), snapshot.Calibration.Brake);
        Assert.Equal(new HardwareCalibrationRange(5, 6), snapshot.Calibration.Clutch);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(snapshot, JsonSerializer.Deserialize<CalibrationSnapshot>(JsonSerializer.Serialize(snapshot, options), options));
    }

    [Fact]
    public async Task MissingConfigurationDoesNotIssueAnyFeatureRequest()
    {
        var features = new TestFeatures();
        var reader = new CalibrationSnapshotReader(new TestSource(PedalInputReportTests.Collection), features);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, features.Calls);
    }

    [Fact]
    public async Task AmbiguousDevicesRequireExplicitSelection()
    {
        var features = new TestFeatures();
        var reader = new CalibrationSnapshotReader(new TestSource(PedalCalibrationReportTests.Collection,
            PedalCalibrationReportTests.Collection with { Path = "second-device" }), features);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, features.Calls);
        var snapshot = await reader.ReadAsync(TimeSpan.FromSeconds(2), "SECOND-DEVICE");
        Assert.Equal("second-device", snapshot.Device.Path);
        Assert.Equal(1, features.Calls);
    }

    [Fact]
    public async Task TimeoutCancelsTheFeatureRead()
    {
        var features = new TestFeatures { WaitForCancellation = true };
        var reader = new CalibrationSnapshotReader(new TestSource(PedalCalibrationReportTests.Collection), features);
        await Assert.ThrowsAsync<TimeoutException>(() => reader.ReadAsync(TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(features.Cancelled);
    }

    [Fact]
    public async Task UserCancellationIsNotReportedAsATimeout()
    {
        var features = new TestFeatures { WaitForCancellation = true };
        var reader = new CalibrationSnapshotReader(new TestSource(PedalCalibrationReportTests.Collection), features);
        using var cancellation = new CancellationTokenSource();
        var pending = reader.ReadAsync(TimeSpan.FromSeconds(2), cancellationToken: cancellation.Token);
        await features.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(features.Cancelled);
    }

    [Fact]
    public async Task APreCancelledRequestDoesNotAccessHardware()
    {
        var features = new TestFeatures();
        var reader = new CalibrationSnapshotReader(new TestSource(PedalCalibrationReportTests.Collection), features);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(TimeSpan.FromSeconds(2), cancellationToken: cancellation.Token));
        Assert.Equal(0, features.Calls);
    }

    [Fact]
    public async Task DeviceRemovalFailsWithoutReturningDefaultCalibration()
    {
        var features = new TestFeatures { Failure = new IOException("Device removed.") };
        var reader = new CalibrationSnapshotReader(new TestSource(PedalCalibrationReportTests.Collection), features);
        var exception = await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("Device removed.", exception.Message);
    }

    [Fact]
    public async Task TruncatedResponseCannotBecomeASnapshot()
    {
        var features = new TestFeatures { Report = new byte[16] };
        var reader = new CalibrationSnapshotReader(new TestSource(PedalCalibrationReportTests.Collection), features);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class TestSource(params HidDeviceInfo[] collections) : IInputReportSource
    {
        public IReadOnlyList<HidDeviceInfo> Find(ushort vendorId, ushort productId) => collections;

        public IInputReportConnection Open(HidDeviceInfo collection) =>
            throw new InvalidOperationException("Snapshots must not open an input stream.");
    }

    private sealed class TestFeatures : IFeatureReportReader
    {
        public int Calls { get; private set; }
        public byte ReportId { get; private set; }
        public HidDeviceInfo? Collection { get; private set; }
        public bool WaitForCancellation { get; init; }
        public bool Cancelled { get; private set; }
        public IOException? Failure { get; init; }
        public byte[] Report { get; init; } = Convert.FromHexString("0301000200030000000400000005000600");
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<byte[]> ReadAsync(HidDeviceInfo collection, byte reportId, CancellationToken cancellationToken)
        {
            Calls++;
            ReportId = reportId;
            Collection = collection;
            Started.TrySetResult();
            if (Failure is not null)
            {
                throw Failure;
            }

            if (WaitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
            }

            return Report;
        }
    }
}
