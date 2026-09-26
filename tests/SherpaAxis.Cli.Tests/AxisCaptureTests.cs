using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Cli.Tests;

public sealed class AxisCaptureTests
{
    [Fact]
    public async Task AcquisitionFailureClosesTheCsvFileAndPreservesTheOriginalError()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"failed-capture-{Guid.NewGuid():N}.csv");
        try
        {
            var monitor = new PedalMonitor(new AmbiguousSource(), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2));
            var result = await AxisCapture.RunAsync(monitor, new MonitorOptions(TimeSpan.FromSeconds(1), path, null),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType<InvalidOperationException>(result.Error);
            Assert.Contains("Multiple pedals", result.Error.Message, StringComparison.Ordinal);
            Assert.Null(result.RecordedSamples);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using var reader = new StreamReader(stream);
            Assert.Equal("elapsed_seconds,connection,throttle_device_axis,brake_device_axis,clutch_device_axis", reader.ReadLine());
            Assert.Null(reader.ReadLine());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class AmbiguousSource : IInputReportSource
    {
        public IReadOnlyList<HidDeviceInfo> Find(ushort vendorId, ushort productId) =>
        [
            new("first", vendorId, productId, 0x0200, 1, 4, 9, 0, 0),
            new("second", vendorId, productId, 0x0200, 1, 4, 9, 0, 0)
        ];

        public IInputReportConnection Open(HidDeviceInfo collection) => throw new InvalidOperationException("Must select a single device first.");
    }
}
