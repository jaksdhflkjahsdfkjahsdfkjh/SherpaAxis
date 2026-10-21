using System.IO;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services.Hardware;

public sealed record PedalControllerConnection(CalibrationSnapshot Snapshot, HidDeviceIdentity Identity,
    IFeatureReportReader Reader, IFeatureReportWriter Writer,
    Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> Input, IAsyncDisposable Transport) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Transport.DisposeAsync();

    public static async Task<PedalControllerConnection> OpenAsync(CancellationToken token)
    {
        var source = new WindowsHidInputSource();
        var devices = await Task.Run(() => source.Find(PedalInputReport.VendorId, PedalInputReport.ProductId), token).ConfigureAwait(false);
        var configurations = devices.Where(PedalCalibrationReport.Supports).ToArray();
        var inputs = devices.Where(PedalInputReport.Supports).ToArray();
        if (configurations.Length != 1 || inputs.Length != 1)
        { throw new InvalidOperationException("Connect one compatible pedal controller. Close FEEL-VR Control before enabling drift correction."); }
        var features = new WindowsHidFeatureConnection(configurations[0], exclusive: true);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            var identity = await features.ReadIdentityAsync(deadline.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(identity.SerialNumber)) { throw new IOException("Controller serial identity is unavailable."); }
            var parts = PedalDeviceIdentity.Parse(identity.SerialNumber);
            if (parts.Hardware != "PE0" || parts.Firmware != "017")
            { throw new InvalidOperationException("Raw drift correction currently supports PE0 firmware 017 only."); }
            var report = await features.ReadAsync(configurations[0], PedalCalibrationReport.ReportId, deadline.Token).ConfigureAwait(false);
            var bounds = PedalCalibrationReport.Decode(report);
            PedalHardwareSettings.Validate(bounds);
            var snapshot = new CalibrationSnapshot(1, DateTimeOffset.UtcNow, configurations[0], bounds);
            var monitor = new PedalMonitor(new SelectedInputSource(source, inputs[0]), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            return new(snapshot, identity, features, features, inputToken => monitor.WatchAsync(inputs[0].Path, inputToken), features);
        }
        catch (OperationCanceledException failure) when (!token.IsCancellationRequested)
        { await features.DisposeAsync().ConfigureAwait(false); throw new TimeoutException("Controller identity/calibration read timed out.", failure); }
        catch { await features.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private sealed class SelectedInputSource(IInputReportSource source, HidDeviceInfo input) : IInputReportSource
    {
        public IReadOnlyList<HidDeviceInfo> Find(ushort vendorId, ushort productId) =>
            input.VendorId == vendorId && input.ProductId == productId ? [input] : [];
        public IInputReportConnection Open(HidDeviceInfo collection) => source.Open(collection);
    }
}
