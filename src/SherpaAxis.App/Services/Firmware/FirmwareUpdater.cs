using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Hardware;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services.Firmware;

public sealed class FirmwareConnection(RecoveryControllerConnection controller, UsbDeviceLocation usb, PedalCalibration calibration) : IAsyncDisposable
{
    public RecoveryControllerConnection Controller { get; } = controller;
    public UsbDeviceLocation Usb { get; } = usb;
    public PedalCalibration Calibration { get; } = calibration;
    public ValueTask DisposeAsync() => Controller.DisposeAsync();
}
public sealed record FirmwareToolResult(int ExitCode, string Output, string Error, bool ReadbackRequested = true);
public sealed record FirmwarePlan(string ImagePath, string ToolPath, string ToolHash, PedalFirmwareImage Image,
    string SerialNumber, string OriginalVersion, string LocationPath, HidDeviceInfo Device, PedalCalibration Calibration);
public sealed record FirmwareUpdateRecord(int SchemaVersion, DateTimeOffset StartedAtUtc, FirmwarePlan Plan,
    string StagedImagePath, string BackupPath, bool ProgramVerified, string Phase, string Log = "", bool DownloadCompleted = false,
    string? ConfirmedSerialNumber = null, bool SerialAutomaticallyAssociated = false);
public sealed record FirmwareCheckResult(bool Complete, bool CanAccept, string Detail);
public sealed record FirmwareUpdateServices(
    Func<CancellationToken, Task<FirmwareConnection>> Connect,
    Func<IReadOnlyList<UsbDeviceLocation>> Bootloaders,
    Func<string, CancellationToken, Task> CheckTool,
    Func<string, string, IProgress<string>, Task<FirmwareToolResult>> Program,
    Func<TimeSpan, CancellationToken, Task>? Delay = null,
    Func<byte[], PedalFirmwareImage>? ValidateImage = null,
    Func<UsbDeviceLocation, Task>? PrepareBootloader = null);

public sealed class FirmwareUpdater(string directory, FirmwareUpdateServices services)
{
    internal const string ReconnectInstructions = "Firmware programming finished, but the pedals have not reconnected. Unplug the pedal USB cable for 5 seconds, reconnect it to the same USB port, then choose Check firmware to restore calibration and finish. The backup is retained; output remains paused.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string JournalPath => Path.Combine(DirectoryPath, "update.json");
    public static string DefaultDirectory => AppDataPaths.FirmwareUpdates;
    public static FirmwareUpdater Default() => new(DefaultDirectory, WindowsFirmwareServices.Create());

    public async Task<HardwareRecoverySnapshot> BackupCalibrationAsync(CancellationToken token)
    {
        if (await ReadPendingAsync(token).ConfigureAwait(false) is not null)
        { throw new IOException("Resolve the previous firmware update before backing up calibration."); }
        await using var connection = await services.Connect(token).ConfigureAwait(false);
        ValidateController(connection);
        return new(2, DateTimeOffset.UtcNow, connection.Controller.Device, connection.Calibration,
            connection.Calibration, null, SerialNumber: connection.Controller.SerialNumber, IsManualBackup: true);
    }

    public async Task<string> CheckFirmwareAsync(CancellationToken token)
    {
        await using var connection = await services.Connect(token).ConfigureAwait(false);
        var identity = ValidateController(connection);
        return $"Controller: {connection.Controller.SerialNumber}\nFirmware: {identity.Firmware}\n" +
            (identity.Firmware == "017" ? "Firmware 017 is installed." : "Firmware 017 is available. Choose Update firmware to install it.");
    }

    public async Task<FirmwarePlan> PrepareAsync(string imagePath, string toolPath, CancellationToken token)
    {
        if (await ReadPendingAsync(token).ConfigureAwait(false) is not null)
        { throw new InvalidOperationException("Resolve the previous firmware update before starting another one."); }
        var bytes = await ReadImageAsync(imagePath, token).ConfigureAwait(false);
        var image = ValidateImage(bytes);
        await services.CheckTool(toolPath, token).ConfigureAwait(false);
        var toolHash = await HashToolAsync(toolPath, token).ConfigureAwait(false);
        await using var connection = await services.Connect(token).ConfigureAwait(false);
        var identity = ValidateController(connection);
        if (services.Bootloaders().Count != 0) { throw new IOException("Disconnect other DFU devices before preparing a pedal firmware update."); }
        return new(Path.GetFullPath(imagePath), Path.GetFullPath(toolPath), toolHash, image, connection.Controller.SerialNumber,
            identity.Firmware, connection.Usb.LocationPath, connection.Controller.Device, connection.Calibration);
    }

    public async Task<FirmwareUpdateRecord> FlashAsync(FirmwarePlan plan, IProgress<string> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(progress);
        if (await ReadPendingAsync(token).ConfigureAwait(false) is not null) { throw new IOException("A previous firmware update still needs review."); }
        var bytes = await ReadImageAsync(plan.ImagePath, token).ConfigureAwait(false);
        if (ValidateImage(bytes) != plan.Image) { throw new IOException("The firmware image changed after preparation. Prepare it again."); }
        await services.CheckTool(plan.ToolPath, token).ConfigureAwait(false);
        if (await HashToolAsync(plan.ToolPath, token).ConfigureAwait(false) != plan.ToolHash) { throw new IOException("The DfuSe tool changed after preparation."); }
        await using var connection = await services.Connect(token).ConfigureAwait(false);
        var identity = ValidateController(connection);
        if (!string.Equals(connection.Controller.SerialNumber, plan.SerialNumber, StringComparison.Ordinal)
            || connection.Usb.LocationPath != plan.LocationPath || identity.Firmware != plan.OriginalVersion
            || connection.Calibration != plan.Calibration || connection.Controller.Device != plan.Device)
        { throw new IOException("The controller or calibration changed after preparation. Prepare the update again."); }
        if (services.Bootloaders().Count != 0) { throw new IOException("Another DFU device is connected; no bootloader command was sent."); }
        var sessionDirectory = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        var staged = Path.Combine(sessionDirectory, "PE0017.dfu");
        await File.WriteAllBytesAsync(staged, bytes, token).ConfigureAwait(false);
        var backup = Path.Combine(sessionDirectory, "hardware-backup.json");
        await new HardwareRecoveryStore(backup).WriteAsync(new(2, DateTimeOffset.UtcNow, connection.Controller.Device,
            plan.Calibration, plan.Calibration, null, SerialNumber: plan.SerialNumber, IsManualBackup: true), token).ConfigureAwait(false);
        var record = new FirmwareUpdateRecord(1, DateTimeOffset.UtcNow, plan, staged, backup, false, "Entering bootloader");
        await WriteAsync(record, token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            progress.Report("Entering the pedal bootloader… Keep the USB cable connected.");
            // A cancelled/failed SET_FEATURE may still reach the device. The durable journal already blocks output.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await connection.Controller.Writer.WriteAsync(connection.Controller.Device,
                    PedalCommandReport.Encode(PedalDeviceCommand.EnterBootloader), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or OperationCanceledException or TimeoutException)
            {
                record = record with { Phase = "Waiting for bootloader", Log = "Bootloader command completion uncertain: " + failure.Message };
                await WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
                progress.Report("The pedal disconnected during bootloader entry. Checking its original USB port…");
            }
            // From this point, user/window cancellation must never kill a programming process.
            return await ProgramAsync(record, progress).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            var current = await ReadPendingAsync(CancellationToken.None).ConfigureAwait(false) ?? record;
            await WriteAsync(current with { Phase = "Update needs review", Log = Limit(current.Log + "\n" + failure.Message) }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<FirmwareUpdateRecord> RetryAsync(IProgress<string> progress, CancellationToken token)
    {
        var record = await ReadPendingAsync(token).ConfigureAwait(false) ?? throw new IOException("No firmware update is pending.");
        await services.CheckTool(record.Plan.ToolPath, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return await ProgramAsync(record, progress).ConfigureAwait(false);
    }

    public async Task<string> RestoreCalibrationBackupAsync(CancellationToken token)
    {
        var record = await ReadPendingAsync(token).ConfigureAwait(false) ?? throw new IOException("No firmware update calibration backup is pending.");
        if (services.Bootloaders().Count != 0) { throw new IOException("Reconnect the pedals in normal mode before restoring their calibration backup."); }
        var backup = await ReadOriginalBackupAsync(record, token).ConfigureAwait(false);
        await using var connection = await services.Connect(token).ConfigureAwait(false);
        var identity = ValidateController(connection);
        if (identity.Firmware != record.Plan.Image.Version || connection.Usb.LocationPath != record.Plan.LocationPath)
        { throw new IOException("The reconnected controller, firmware, USB port and saved calibration backup must match the original update."); }
        if (!MatchesController(record, connection.Controller.SerialNumber))
        { record = await AssociateReconnectedControllerAsync(record, connection, token).ConfigureAwait(false); }
        var directory = Path.GetDirectoryName(record.BackupPath)!;
        using var transaction = new PedalConfigurationTransaction(new(1, DateTimeOffset.UtcNow, connection.Controller.Device, connection.Calibration),
            connection.Controller.Reader, connection.Controller.Writer, new HardwareRecoveryStore(Path.Combine(directory, "calibration-restore.json")),
            TimeSpan.FromSeconds(2), connection.Controller.SerialNumber);
        await new HardwareRecoveryStore(Path.Combine(directory, "post-flash-calibration-backup.json"))
            .WriteAsync(await transaction.CreateHardwareBackupAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
        await transaction.RestoreHardwareBackupAsync(backup with
        { Device = connection.Controller.Device, SerialNumber = connection.Controller.SerialNumber }, token).ConfigureAwait(false);
        await transaction.SaveAsync(token).ConfigureAwait(false);
        const string detail = "Original calibration restored, saved to the controller and read back successfully. Choose Check firmware to review the update.";
        await WriteAsync(record with { Log = Limit(record.Log + "\n" + detail) }, CancellationToken.None).ConfigureAwait(false);
        return detail;
    }

    public async Task<FirmwareCheckResult> CompleteUpdateAsync(IProgress<string> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var record = await ReadPendingAsync(token).ConfigureAwait(false) ?? throw new IOException("No firmware update is pending.");
        if (!record.DownloadCompleted && !record.ProgramVerified)
        { throw new IOException("Programming was not confirmed. Automatic calibration restoration is unavailable; review the update logs."); }
        try
        {
            await WriteAsync(record with { Phase = "Checking controller after programming" }, token).ConfigureAwait(false);
            return await CompleteControllerCheckAsync(record, progress, token).ConfigureAwait(false);
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        {
            var latest = await ReadPendingAsync(CancellationToken.None).ConfigureAwait(false) ?? record;
            await WriteAsync(latest with
            {
                Phase = failure.Message == ReconnectInstructions ? "Reconnect pedals" : "Controller check needs attention",
                Log = Limit(latest.Log + "\nController check: " + failure.Message)
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<FirmwareCheckResult> CompleteControllerCheckAsync(FirmwareUpdateRecord record, IProgress<string> progress, CancellationToken token)
    {
        progress.Report("Firmware programmed. Waiting for the pedals to reconnect automatically…");
        var started = Stopwatch.GetTimestamp();
        for (var attempt = 0; attempt < 120 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30); attempt++)
        {
            token.ThrowIfCancellationRequested();
            var bootloaders = services.Bootloaders();
            if (bootloaders.Count > 1 || bootloaders.Count == 1 && bootloaders[0].LocationPath != record.Plan.LocationPath)
            { throw new IOException("The connected bootloader does not uniquely match the recorded pedal USB port. Calibration was not restored."); }
            if (bootloaders.Count == 0)
            {
                FirmwareConnection? connection = null;
                try { connection = await services.Connect(token).ConfigureAwait(false); }
                catch (Exception failure) when (failure is IOException or TimeoutException) { }
                if (connection is not null)
                {
                    bool restore;
                    await using (connection)
                    {
                        var identity = ValidateController(connection);
                        if (identity.Firmware != record.Plan.Image.Version || connection.Usb.LocationPath != record.Plan.LocationPath)
                        { throw new IOException("The reconnected controller, firmware or USB port does not match the update. Calibration was not restored."); }
                        if (!MatchesController(record, connection.Controller.SerialNumber))
                        { record = await AssociateReconnectedControllerAsync(record, connection, token).ConfigureAwait(false); }
                        restore = connection.Calibration != record.Plan.Calibration || HasCalibrationRecovery(record);
                    }
                    if (restore)
                    {
                        progress.Report("Pedals reconnected. Restoring and saving the original calibration…");
                        await RestoreCalibrationBackupAsync(token).ConfigureAwait(false);
                    }
                    progress.Report("Checking firmware and restored calibration…");
                    var result = await CheckConnectionAsync(false, token).ConfigureAwait(false);
                    if (!result.Complete) { throw new IOException(result.Detail); }
                    return result;
                }
            }
            await DelayAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
        }
        throw new IOException(ReconnectInstructions);
    }

    private static bool HasCalibrationRecovery(FirmwareUpdateRecord record) =>
        Directory.GetFileSystemEntries(Path.GetDirectoryName(record.BackupPath)!, "calibration-restore.json").Length != 0;

    private static bool MatchesController(FirmwareUpdateRecord record, string serial) =>
        record.ConfirmedSerialNumber is { } confirmed ? string.Equals(confirmed, serial, StringComparison.Ordinal)
            : PedalDeviceIdentity.SameController(record.Plan.SerialNumber, serial);

    private static async Task<HardwareRecoverySnapshot> ReadOriginalBackupAsync(FirmwareUpdateRecord record, CancellationToken token)
    {
        var backup = await HardwareRecoveryStore.ReadAsync(record.BackupPath, token).ConfigureAwait(false);
        if (!backup.IsManualBackup || backup.Saved != record.Plan.Calibration || backup.Device != record.Plan.Device
            || !PedalDeviceIdentity.SameController(backup.SerialNumber!, record.Plan.SerialNumber))
        { throw new IOException("The calibration backup does not match the original firmware update. It was not applied."); }
        return backup;
    }

    private async Task<FirmwareUpdateRecord> AssociateReconnectedControllerAsync(FirmwareUpdateRecord record, FirmwareConnection connection, CancellationToken token)
    {
        var original = PedalDeviceIdentity.Parse(record.Plan.SerialNumber);
        var current = ValidateController(connection);
        if ((!record.DownloadCompleted && !record.ProgramVerified) || record.ConfirmedSerialNumber is not null
            || original.Hardware != current.Hardware || original.Firmware != record.Plan.OriginalVersion
            || !int.TryParse(original.Firmware, out var version) || version is < 1 or >= 17
            || current.Firmware != record.Plan.Image.Version || connection.Usb.LocationPath != record.Plan.LocationPath)
        { throw new IOException("The reconnected controller identity does not match the update. Calibration was not restored."); }
        _ = await ReadOriginalBackupAsync(record, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        record = record with
        {
            ConfirmedSerialNumber = connection.Controller.SerialNumber,
            SerialAutomaticallyAssociated = true,
            Phase = "Controller rediscovered after programming",
            Log = Limit(record.Log + $"\nAutomatically associated the supported controller at the original USB port after confirmed programming. Previous serial: {record.Plan.SerialNumber}; current serial: {connection.Controller.SerialNumber}.")
        };
        await WriteAsync(record, token).ConfigureAwait(false);
        return record;
    }

    private async Task<FirmwareUpdateRecord> ProgramAsync(FirmwareUpdateRecord record, IProgress<string> progress)
    {
        await WaitForBootloaderAsync(record.Plan.LocationPath, progress).ConfigureAwait(false);
        var candidates = services.Bootloaders();
        if (candidates.Count != 1 || !candidates[0].DriverReady || candidates[0].LocationPath != record.Plan.LocationPath)
        { throw new IOException("Connect only the original pedal bootloader at its recorded USB port before retrying."); }
        // Keep the exact staged file open without write/delete sharing throughout the external tool's lifetime.
        using var imageLock = FirmwareImageLease.Open(record.StagedImagePath);
        var bytes = await ReadImageAsync(record.StagedImagePath, CancellationToken.None).ConfigureAwait(false);
        if (ValidateImage(bytes) != record.Plan.Image) { throw new IOException("The staged firmware changed; programming was not started."); }
        await using var toolLock = new FileStream(record.Plan.ToolPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(await SHA256.HashDataAsync(toolLock).ConfigureAwait(false)) != record.Plan.ToolHash)
        { throw new IOException("The DfuSe tool changed; programming was not started."); }
        record = record with
        { Phase = "Programming firmware", ProgramVerified = false, DownloadCompleted = false, ConfirmedSerialNumber = null, SerialAutomaticallyAssociated = false };
        await WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        progress.Report("Programming firmware… Do not unplug the pedals or close the app.");
        FirmwareToolResult result;
        try { result = await services.Program(record.Plan.ToolPath, record.StagedImagePath, progress).ConfigureAwait(false); }
        catch (Exception failure)
        {
            await WriteAsync(record with { Phase = "Programming needs review", Log = Limit(record.Log + "\n" + failure.Message) }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        var verified = result.ReadbackRequested && VerifyToolResult(result);
        var downloaded = result.ExitCode == 0 && result.Output.Split('\n').Any(line => line.Trim() == "Upgrade successful !");
        var completed = verified || !result.ReadbackRequested && downloaded;
        record = record with
        {
            ProgramVerified = verified,
            DownloadCompleted = completed,
            Phase = completed ? "Awaiting controller check" : "Programming needs review",
            Log = Limit(record.Log + "\n" + result.Output + "\n" + result.Error + (result.ReadbackRequested ? "" : "\nByte readback was not performed; the pedal firmware restarts after download."))
        };
        await WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        if (!completed) { throw new IOException("DfuSe did not confirm successful programming. Output remains paused; inspect the update log before retrying."); }
        progress.Report(verified ? "Programming verified. Waiting for the pedals to reconnect automatically…"
            : "Firmware programming completed. Waiting for the pedals to reconnect automatically…");
        return record;
    }

    private async Task WaitForBootloaderAsync(string location, IProgress<string> progress)
    {
        var prepared = false;
        var detected = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var candidates = services.Bootloaders();
            if (candidates.Count > 1 || candidates.Count == 1 && candidates[0].LocationPath != location)
            { throw new IOException("The DFU device does not uniquely match the original pedal USB port. Programming was not started."); }
            if (candidates.Count == 1)
            {
                detected = true;
                if (candidates[0].DriverReady) { return; }
                if (!prepared && services.PrepareBootloader is not null)
                {
                    prepared = true;
                    progress.Report("The pedal bootloader is connected. Preparing its DfuSe USB driver; approve Windows administrator access if requested.");
                    await services.PrepareBootloader(candidates[0]).ConfigureAwait(false);
                }
            }
            await DelayAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
        }
        throw new IOException(detected
            ? "The pedal bootloader is connected, but its DfuSe USB driver is not ready. Run SherpaAxis Setup to repair it, then choose Retry flash. The backup and update record were retained."
            : "The pedal bootloader did not appear at its recorded USB port. Reconnect the pedals at that port and choose Retry flash. The backup and update record were retained.");
    }

    public async Task<FirmwareCheckResult> CheckConnectionAsync(bool acceptCurrent, CancellationToken token)
    {
        var record = await ReadPendingAsync(token).ConfigureAwait(false);
        if (record is null) { return new(true, false, "No firmware update is pending."); }
        if (services.Bootloaders().Count != 0) { return new(false, false, "The pedals are still in DFU mode. After verified programming, unplug and reconnect them; otherwise retry programming at the recorded USB port."); }
        await using var connection = await services.Connect(token).ConfigureAwait(false);
        var identity = ValidateController(connection);
        if (identity.Firmware != record.Plan.Image.Version)
        { throw new IOException("The reconnected controller identity or firmware does not match the prepared update."); }
        if (connection.Usb.LocationPath != record.Plan.LocationPath)
        { return new(false, false, "Reconnect the original controller at its recorded USB port, then choose Check firmware. Output remains paused."); }
        if (!MatchesController(record, connection.Controller.SerialNumber))
        { record = await AssociateReconnectedControllerAsync(record, connection, token).ConfigureAwait(false); }
        if (connection.Calibration != record.Plan.Calibration)
        { return new(false, false, "Firmware 017 is detected, but calibration does not match the pre-update backup. Restore the calibration backup, then choose Check firmware. Output remains paused."); }
        if (HasCalibrationRecovery(record))
        { return new(false, false, "Calibration restoration still needs verification. Restore the calibration backup again before resuming output."); }
        if (!record.ProgramVerified && !record.DownloadCompleted && !acceptCurrent)
        {
            return new(false, true, "The original controller reports firmware 017 and calibration matches the backup, but programming was not confirmed. Review the update logs. Use detected controller resumes output without claiming a verified flash.");
        }
        token.ThrowIfCancellationRequested();
        await WriteAsync(record with { Phase = acceptCurrent ? "Detected firmware and calibration explicitly accepted" : "Controller verified after reconnect" }, token).ConfigureAwait(false);
        File.Delete(JournalPath);
        return new(true, false, (record.ConfirmedSerialNumber is null
            ? "Firmware 017 and controller identity verified. Hardware backup and update log were retained."
            : record.SerialAutomaticallyAssociated
                ? "Firmware 017 and restored calibration checked. The controller was rediscovered at the original USB port. Hardware backup and update log were retained."
                : "Firmware 017 and restored calibration checked. The changed serial number was confirmed by the user. Hardware backup and update log were retained.")
            + (record.ProgramVerified ? "" : " Byte readback was not verified."));
    }

    public async Task<FirmwareUpdateRecord?> ReadPendingAsync(CancellationToken token)
    {
        string[] entries;
        // Enumerating detects permission failures instead of treating them as an empty journal.
        try { entries = Directory.GetFileSystemEntries(DirectoryPath, "update.json"); }
        catch (DirectoryNotFoundException) when (!File.Exists(DirectoryPath)) { return null; }
        if (entries.Length == 0) { return null; }
        await using var stream = File.OpenRead(JournalPath);
        if (stream.Length > 262144) { throw new InvalidDataException("Firmware update record is too large."); }
        FirmwareUpdateRecord record;
        try { record = await JsonSerializer.DeserializeAsync<FirmwareUpdateRecord>(stream, JsonOptions, token).ConfigureAwait(false) ?? throw new InvalidDataException("Firmware update record is empty."); }
        catch (JsonException failure) { throw new InvalidDataException("Firmware update record is corrupt; keep it for recovery.", failure); }
        if (record.SchemaVersion != 1 || record.Plan is null || record.Plan.Image is null || string.IsNullOrWhiteSpace(record.Plan.SerialNumber)
            || record.Plan.Image.Version != "017" || services.ValidateImage is null && record.Plan.Image.Sha256 != PedalFirmwareImage.Reviewed017Hash
            || string.IsNullOrWhiteSpace(record.Plan.LocationPath)
            || !UnderDirectory(record.StagedImagePath) || !UnderDirectory(record.BackupPath) || !Path.IsPathFullyQualified(record.Plan.ToolPath))
        { throw new InvalidDataException("Firmware update identity or paths are invalid; keep the record for recovery."); }
        PedalHardwareSettings.Validate(record.Plan.Calibration);
        if (record.SerialAutomaticallyAssociated && record.ConfirmedSerialNumber is null)
        { throw new InvalidDataException("The confirmed controller identity is invalid for this update. Keep the record for recovery."); }
        if (record.ConfirmedSerialNumber is { } confirmed)
        {
            var original = PedalDeviceIdentity.Parse(record.Plan.SerialNumber);
            var identity = PedalDeviceIdentity.Parse(confirmed);
            if ((!record.DownloadCompleted && !record.ProgramVerified) || identity.Hardware != original.Hardware
                || identity.Firmware != record.Plan.Image.Version || original.Firmware != record.Plan.OriginalVersion
                || !int.TryParse(original.Firmware, out var version) || version is < 1 or >= 17)
            { throw new InvalidDataException("The confirmed controller identity is invalid for this update. Keep the record for recovery."); }
        }
        return record;
    }

    private bool UnderDirectory(string path) => !string.IsNullOrWhiteSpace(path) && Path.GetFullPath(path).StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private Task DelayAsync(TimeSpan duration, CancellationToken token) => services.Delay?.Invoke(duration, token) ?? Task.Delay(duration, token);
    private PedalFirmwareImage ValidateImage(byte[] bytes) => services.ValidateImage?.Invoke(bytes) ?? PedalFirmwareImage.Validate(bytes);
    private static PedalDeviceIdentity ValidateController(FirmwareConnection connection)
    {
        var identity = PedalDeviceIdentity.Parse(connection.Controller.SerialNumber);
        if (!PedalCalibrationReport.Supports(connection.Controller.Device) || identity.Hardware != "PE0" || !int.TryParse(identity.Firmware, out var version) || version is < 1 or > 17
            || string.IsNullOrWhiteSpace(connection.Usb.LocationPath))
        { throw new IOException("Firmware updating supports identified PE0 pedals with firmware 001–017. Newer or unknown controllers are not downgraded."); }
        PedalHardwareSettings.Validate(connection.Calibration);
        return identity;
    }
    private static async Task<byte[]> ReadImageAsync(string path, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = File.OpenRead(path);
        if (stream.Length is < 1 or > PedalFirmwareImage.MaximumLength) { throw new InvalidDataException("Firmware file is empty or too large."); }
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }
    private static async Task<string> HashToolAsync(string path, CancellationToken token)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)); }
    public static bool VerifyToolResult(FirmwareToolResult result) => result.ExitCode == 0
        && result.Output.Split('\n').Any(line => line.Trim() == "Upgrade successful !")
        && result.Output.Split('\n').Any(line => line.Trim() == "Verify successful !");
    private static string Limit(string value) => value.Length > 60000 ? value[^60000..] : value;
    private async Task WriteAsync(FirmwareUpdateRecord record, CancellationToken token)
    {
        await AtomicFile.WriteAsync(JournalPath, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions), token: token).ConfigureAwait(false);
        File.Copy(JournalPath, Path.Combine(Path.GetDirectoryName(record.StagedImagePath)!, "update-log.json"), true);
    }
}
