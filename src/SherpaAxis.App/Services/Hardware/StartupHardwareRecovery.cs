using System.IO;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services.Hardware;

public sealed record PendingHardwareRecovery(string Path, HardwareRecoverySnapshot? Snapshot, string Detail, bool CanRestore = false, bool CanSave = false)
{
    public string Label => Snapshot is { } snapshot ? $"{snapshot.SerialNumber ?? "?"} · {snapshot.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {System.IO.Path.GetFileName(Path)}" : System.IO.Path.GetFileName(Path);
    public bool IsDriftMonitoring => IsDriftRecord(Path);
    internal static bool IsDriftRecord(string path) => System.IO.Path.GetFileName(path).StartsWith("raw-", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A held, identity-checked feature connection, injectable without opening hardware in tests.</summary>
public sealed class RecoveryControllerConnection(HidDeviceInfo device, string serialNumber, IFeatureReportReader reader,
    IFeatureReportWriter writer, IAsyncDisposable? transport = null) : IAsyncDisposable
{
    public HidDeviceInfo Device { get; } = device;
    public string SerialNumber { get; } = serialNumber;
    public IFeatureReportReader Reader { get; } = reader;
    public IFeatureReportWriter Writer { get; } = writer;
    public ValueTask DisposeAsync() => transport?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public sealed class StartupHardwareRecovery(string directory,
    Func<HardwareRecoverySnapshot, CancellationToken, Task<RecoveryControllerConnection>> connect)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public static StartupHardwareRecovery Default() => new(HardwareRecoveryStore.DefaultDirectory, ConnectAsync);

    public async Task<IReadOnlyList<PendingHardwareRecovery>> CheckAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try { return await CheckCoreAsync(deadline.Token); }
        catch (OperationCanceledException failure) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Recovery verification timed out. Output remains paused; connect the controller and check again.", failure); }
    }

    private async Task<IReadOnlyList<PendingHardwareRecovery>> CheckCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string[] paths;
        try { paths = Directory.EnumerateFiles(DirectoryPath, "*.json").Take(129).ToArray(); }
        catch (DirectoryNotFoundException) when (!File.Exists(DirectoryPath)) { return []; }
        if (paths.Length > 128) { throw new IOException("Too many recovery files. Review the recovery folder before continuing."); }
        var pending = new List<PendingHardwareRecovery>();
        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            HardwareRecoverySnapshot? snapshot = null;
            try
            {
                snapshot = await HardwareRecoveryStore.ReadAsync(path, token);
                if (snapshot.IsManualBackup) { continue; }
                await using var connection = await OpenVerifiedAsync(snapshot, token);
                var actual = await ReadAsync(connection, token);
                if (actual == snapshot.Saved && !snapshot.SaveUncertain)
                {
                    // A restart/reconnect may already have restored the saved bounds. No SET_REPORT is needed.
                    await new HardwareRecoveryStore(path).ClearAsync(token);
                    continue;
                }
                var known = actual == snapshot.Saved || actual == snapshot.LastConfirmed || actual == snapshot.Attempted;
                var detail = !known ? "Controller settings differ from this recovery record. Automatic restoration is unavailable; keep the file for review."
                    : snapshot.SaveUncertain && actual == snapshot.Saved
                        ? "Original bounds are verified, but the previous device Save was uncertain. Save the restored calibration explicitly before resuming output."
                        : PendingHardwareRecovery.IsDriftRecord(path)
                            ? "Automatic drift monitoring was interrupted while the controller used temporary raw-input bounds. This does not mean you started a calibration. Close FEEL-VR Control and games, then restore the original calibration from the backup."
                        : "An interrupted calibration left temporary controller bounds. Close FEEL-VR Control and games, then restore the original calibration.";
                pending.Add(new(path, snapshot, detail, known, snapshot.SaveUncertain && actual == snapshot.Saved));
            }
            catch (Exception failure) when (Expected(failure) && !token.IsCancellationRequested)
            { pending.Add(new(path, snapshot, $"Recovery verification failed: {failure.Message}")); }
        }
        return pending;
    }

    public Task RestoreAsync(string path, CancellationToken token = default) => ResolveAsync(path, false, token);
    public Task SaveRestoredAsync(string path, CancellationToken token = default) => ResolveAsync(path, true, token);
    private async Task ResolveAsync(string path, bool save, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), DirectoryPath, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("Select a recovery record from the recovery folder."); }
        var snapshot = await HardwareRecoveryStore.ReadAsync(fullPath, token);
        if (snapshot.IsManualBackup) { throw new InvalidOperationException("Manual backups are not interrupted calibration records."); }
        await using var connection = await OpenVerifiedAsync(snapshot, token);
        if (save && (!snapshot.SaveUncertain || await ReadAsync(connection, token) != snapshot.Saved))
        { throw new InvalidOperationException("Restore and verify the original bounds before saving recovery settings."); }
        using var transaction = PedalConfigurationTransaction.FromRecovery(snapshot with { Device = connection.Device },
            connection.Reader, connection.Writer, new HardwareRecoveryStore(fullPath), TimeSpan.FromSeconds(2), connection.SerialNumber);
        await transaction.RestoreSavedAsync(token);
        if (save) { await transaction.SaveAsync(token); }
    }

    private async Task<RecoveryControllerConnection> OpenVerifiedAsync(HardwareRecoverySnapshot snapshot, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(snapshot.SerialNumber))
        { throw new InvalidOperationException("This recovery record has no verified serial number. Keep it for manual review."); }
        var connection = await connect(snapshot, token);
        try
        {
            if (!PedalDeviceIdentity.SameController(snapshot.SerialNumber, connection.SerialNumber)
                || snapshot.Device with { Path = connection.Device.Path } != connection.Device)
            { throw new IOException("Recovery requires the same physical controller and matching HID capabilities."); }
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task<PedalCalibration> ReadAsync(RecoveryControllerConnection connection, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var actual = PedalCalibrationReport.Decode(await connection.Reader.ReadAsync(connection.Device, PedalCalibrationReport.ReportId, deadline.Token));
        PedalHardwareSettings.Validate(actual);
        return actual;
    }

    private static async Task<RecoveryControllerConnection> ConnectAsync(HardwareRecoverySnapshot snapshot, CancellationToken token)
    {
        var devices = await Task.Run(() => new WindowsHidInputSource().Find(PedalInputReport.VendorId, PedalInputReport.ProductId)
            .Where(PedalCalibrationReport.Supports).ToArray(), token);
        // Do not guess between multiple physical controllers or fall back to an old device path.
        if (devices.Length != 1) { throw new InvalidOperationException("Connect only the controller named in the recovery record, then check again."); }
        var features = new WindowsHidFeatureConnection(devices[0]);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            var identity = await features.ReadIdentityAsync(deadline.Token);
            if (!PedalDeviceIdentity.SameController(snapshot.SerialNumber!, identity.SerialNumber))
            { throw new IOException("The connected controller does not match the recovery serial number."); }
            var parts = PedalDeviceIdentity.Parse(identity.SerialNumber);
            if (parts.Hardware != "PE0" || parts.Firmware != "017")
            { throw new InvalidOperationException("Native calibration currently targets the analyzed PE0/017 controller."); }
            return new(devices[0], identity.SerialNumber, features, features, features);
        }
        catch { await features.DisposeAsync(); throw; }
    }

    private static bool Expected(Exception failure) => failure is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or TimeoutException or OperationCanceledException or System.Security.SecurityException;
}
