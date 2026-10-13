using System.IO;
using System.Windows;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services;

public sealed class HardwareCalibrationSession(PedalConfigurationTransaction configuration, HardwareCalibrationView view,
    IAsyncDisposable? transport = null, string? backupPath = null) : IAsyncDisposable
{
    private bool disposed;
    public HardwareCalibrationView View { get; } = view;
    public string? BackupPath { get; } = backupPath;

    public static async Task<HardwareCalibrationSession> ConnectAsync(PedalChannel selectedChannel, ResourceDictionary theme, CancellationToken token,
        HardwareCalibrationView? cachedView = null)
    {
        var connection = await Task.Run(() => ReadConnectionAsync(token), token);
        PedalConfigurationTransaction? configuration = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var backup = new HardwareRecoveryStore(Path.Combine(HardwareRecoveryStore.DefaultDirectory, $"pedals-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
            configuration = new(connection.Snapshot, connection.Features, connection.Features, backup, TimeSpan.FromSeconds(2), connection.Identity.SerialNumber);
            var monitor = new PedalMonitor(connection.Source, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            Func<CancellationToken, IAsyncEnumerable<PedalMonitorEvent>> input = inputToken => monitor.WatchAsync(connection.InputPath, inputToken);
            var view = cachedView;
            if (view is null) { view = new(configuration, input, theme, connection.Identity, selectedChannel); }
            else { view.Reconnect(configuration, input, connection.Identity, selectedChannel); }
            return new(configuration, view, connection.Features, backup.FilePath);
        }
        catch { configuration?.Dispose(); await connection.Features.DisposeAsync(); throw; }
    }

    private sealed record HardwareConnection(WindowsHidFeatureConnection Features, WindowsHidInputSource Source,
        string InputPath, HidDeviceIdentity Identity, CalibrationSnapshot Snapshot);

    private static async Task<HardwareConnection> ReadConnectionAsync(CancellationToken token)
    {
        var source = new WindowsHidInputSource();
        var devices = source.Find(PedalInputReport.VendorId, PedalInputReport.ProductId);
        var collections = devices.Where(PedalCalibrationReport.Supports).ToArray();
        var inputs = devices.Where(PedalInputReport.Supports).ToArray();
        token.ThrowIfCancellationRequested();
        if (collections.Length != 1 || inputs.Length != 1) { throw new InvalidOperationException("Connect one compatible pedal controller, then click Connect controller."); }
        var features = new WindowsHidFeatureConnection(collections[0]);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            var identity = await features.ReadIdentityAsync(deadline.Token);
            if (string.IsNullOrWhiteSpace(identity.SerialNumber)) { throw new InvalidOperationException("Controller has no readable serial identity; hardware writes are unavailable."); }
            var parts = PedalDeviceIdentity.Parse(identity.SerialNumber);
            if (parts.Hardware != "PE0" || parts.Firmware != "017") { throw new InvalidOperationException("Native calibration currently targets the analyzed PE0/017 controller."); }
            // The collection and its held handle are already verified; avoid two more HID enumerations.
            using var settingsDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            settingsDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            byte[] report;
            try { report = await features.ReadAsync(collections[0], PedalCalibrationReport.ReportId, settingsDeadline.Token); }
            catch (OperationCanceledException failure) when (!token.IsCancellationRequested)
            { throw new TimeoutException("Calibration feature read timed out.", failure); }
            var snapshot = new CalibrationSnapshot(1, DateTimeOffset.UtcNow, collections[0], PedalCalibrationReport.Decode(report));
            token.ThrowIfCancellationRequested();
            return new(features, source, inputs[0].Path, identity, snapshot);
        }
        catch { await features.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        if (!await View.TryCloseAsync()) { throw new IOException("Controller restoration must finish before leaving calibration."); }
        await View.DisposeAsync();
        configuration.Dispose();
        if (transport is not null) { await transport.DisposeAsync(); }
        disposed = true;
        GC.SuppressFinalize(this);
    }
}
