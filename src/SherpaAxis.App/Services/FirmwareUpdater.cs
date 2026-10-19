using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services;

public sealed class FirmwareConnection(RecoveryControllerConnection controller, UsbDeviceLocation usb, PedalCalibration calibration) : IAsyncDisposable
{
    public RecoveryControllerConnection Controller { get; } = controller;
    public UsbDeviceLocation Usb { get; } = usb;
    public PedalCalibration Calibration { get; } = calibration;
    public ValueTask DisposeAsync() => Controller.DisposeAsync();
}
public sealed record FirmwareToolResult(int ExitCode, string Output, string Error);
public sealed record FirmwarePlan(string ImagePath, string ToolPath, string ToolHash, PedalFirmwareImage Image,
    string SerialNumber, string OriginalVersion, string LocationPath, HidDeviceInfo Device, PedalCalibration Calibration);
public sealed record FirmwareUpdateRecord(int SchemaVersion, DateTimeOffset StartedAtUtc, FirmwarePlan Plan,
    string StagedImagePath, string BackupPath, bool ProgramVerified, string Phase, string Log = "");
public sealed record FirmwareCheckResult(bool Complete, bool CanAccept, string Detail);
public sealed record FirmwareUpdateServices(
    Func<CancellationToken, Task<FirmwareConnection>> Connect,
    Func<IReadOnlyList<UsbDeviceLocation>> Bootloaders,
    Func<string, CancellationToken, Task> CheckTool,
    Func<string, string, IProgress<string>, Task<FirmwareToolResult>> Program,
    Func<TimeSpan, CancellationToken, Task>? Delay = null,
    Func<byte[], PedalFirmwareImage>? ValidateImage = null);

public sealed class FirmwareUpdater(string directory, FirmwareUpdateServices services)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string JournalPath => Path.Combine(DirectoryPath, "update.json");
    public static string DefaultDirectory => AppDataPaths.FirmwareUpdates;
    public static FirmwareUpdater Default() => new(DefaultDirectory, WindowsFirmwareServices.Create());

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
            await connection.Controller.Writer.WriteAsync(connection.Controller.Device,
                PedalCommandReport.Encode(PedalDeviceCommand.EnterBootloader), deadline.Token).ConfigureAwait(false);
            // From this point, user/window cancellation must never kill a programming process.
            var found = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var candidates = services.Bootloaders();
                if (candidates.Count > 1 || candidates.Count == 1 && candidates[0].LocationPath != plan.LocationPath)
                { throw new IOException("The DFU device does not uniquely match the original pedal USB port. Programming was not started."); }
                if (candidates.Count == 1 && candidates[0].DriverReady) { found = true; break; }
                await DelayAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
            }
            if (!found) { throw new IOException("The pedal bootloader did not appear. Check the DfuSe USB driver; the update record was retained."); }
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

    private async Task<FirmwareUpdateRecord> ProgramAsync(FirmwareUpdateRecord record, IProgress<string> progress)
    {
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
        record = record with { Phase = "Programming and verifying", ProgramVerified = false, Log = "" };
        await WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        progress.Report("Programming and verifying firmware… Do not unplug the pedals or close the app.");
        FirmwareToolResult result;
        try { result = await services.Program(record.Plan.ToolPath, record.StagedImagePath, progress).ConfigureAwait(false); }
        catch (Exception failure)
        {
            await WriteAsync(record with { Phase = "Programming needs review", Log = Limit(failure.Message) }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        var verified = VerifyToolResult(result);
        record = record with { ProgramVerified = verified, Phase = verified ? "Awaiting reconnect" : "Programming needs review", Log = Limit(result.Output + "\n" + result.Error) };
        await WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        if (!verified) { throw new IOException("DfuSe did not confirm both programming and byte verification. Output remains paused; inspect the update log before retrying."); }
        progress.Report("Programming verified. Unplug and reconnect the pedals, then click Check updated controller.");
        return record;
    }

    public async Task<FirmwareCheckResult> CheckConnectionAsync(bool acceptCurrent, CancellationToken token)
    {
        var record = await ReadPendingAsync(token).ConfigureAwait(false);
        if (record is null) { return new(true, false, "No firmware update is pending."); }
        if (services.Bootloaders().Count != 0) { return new(false, false, "The pedals are still in DFU mode. After verified programming, unplug and reconnect them; otherwise retry programming at the recorded USB port."); }
        await using var connection = await services.Connect(token).ConfigureAwait(false);
        var identity = ValidateController(connection);
        if (!PedalDeviceIdentity.SameController(record.Plan.SerialNumber, connection.Controller.SerialNumber) || identity.Firmware != record.Plan.Image.Version)
        { throw new IOException("The reconnected controller identity or firmware does not match the prepared update."); }
        if (!record.ProgramVerified && !acceptCurrent)
        { return new(false, true, "The original controller reports firmware 017, but the previous programming result is unverified. Retry the update or explicitly accept the detected firmware and calibration."); }
        if (connection.Calibration != record.Plan.Calibration && !acceptCurrent)
        { return new(false, true, "Firmware 017 is detected, but hardware calibration changed. Calibrate the pedals or review the backup, then explicitly accept the current calibration."); }
        token.ThrowIfCancellationRequested();
        await WriteAsync(record with { Phase = acceptCurrent ? "Detected firmware and calibration explicitly accepted" : "Controller verified after reconnect" }, token).ConfigureAwait(false);
        File.Delete(JournalPath);
        return new(true, false, "Firmware 017 and controller identity verified. Hardware backup and update log were retained.");
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
