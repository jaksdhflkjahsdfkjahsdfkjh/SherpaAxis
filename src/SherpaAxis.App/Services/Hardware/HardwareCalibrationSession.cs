using System.IO;
using System.Windows;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services.Hardware;

public sealed class HardwareCalibrationSession(PedalConfigurationTransaction configuration, HardwareCalibrationView view,
    IAsyncDisposable? transport = null, string? backupPath = null) : IAsyncDisposable
{
    private bool disposed;
    public HardwareCalibrationView View { get; } = view;
    public string? BackupPath { get; } = backupPath;

    public static async Task<HardwareCalibrationSession> ConnectAsync(PedalChannel selectedChannel, ResourceDictionary theme, CancellationToken token,
        HardwareCalibrationView? cachedView = null)
    {
        var connection = await PedalControllerConnection.OpenAsync(token);
        PedalConfigurationTransaction? configuration = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var backup = new HardwareRecoveryStore(Path.Combine(HardwareRecoveryStore.DefaultDirectory, $"pedals-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
            configuration = new(connection.Snapshot, connection.Reader, connection.Writer, backup, TimeSpan.FromSeconds(2), connection.Identity.SerialNumber);
            var input = connection.Input;
            var view = cachedView;
            if (view is null) { view = new(configuration, input, theme, connection.Identity, selectedChannel); }
            else { view.Reconnect(configuration, input, connection.Identity, selectedChannel); }
            var references = new SensorReferenceStore(SensorReferenceStore.DefaultDirectory);
            view.StoreSensorReference = (channel, raw, applied) => references.WriteAsync(new(connection.Identity.SerialNumber, channel, raw, applied));
            return new(configuration, view, connection, backup.FilePath);
        }
        catch { configuration?.Dispose(); await connection.DisposeAsync(); throw; }
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
