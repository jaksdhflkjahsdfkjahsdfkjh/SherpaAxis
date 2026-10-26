using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Firmware;
using SherpaAxis.App.Services.Hardware;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Tests;

public sealed class FirmwareUpdateTests
{
    [Fact]
    public void BundledCatalogOnlyOffersReviewedImagesAndReportsInvalidFiles()
    {
        var bundled = FirmwareCatalog.Scan(WindowsFirmwareServices.FirmwareDirectory);
        var approved = Assert.Single(bundled.Images);
        Assert.Equal("017", approved.Version);
        Assert.Equal("PE0017.dfu", approved.FileName);
        Assert.Empty(bundled.RejectedFiles);
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(approved.Path, Path.Combine(directory, approved.FileName));
            File.WriteAllBytes(Path.Combine(directory, "corrupt.dfu"), [1, 2]);
            File.WriteAllBytes(Path.Combine(directory, "unreviewed.dfu"), SyntheticImage());
            File.WriteAllText(Path.Combine(directory, "ignored.txt"), "not firmware");
            using (var oversized = File.Create(Path.Combine(directory, "oversized.dfu"))) { oversized.SetLength(PedalFirmwareImage.MaximumLength + 1); }
            var catalog = FirmwareCatalog.Scan(directory);
            Assert.Single(catalog.Images);
            string[] rejected = ["corrupt.dfu", "oversized.dfu", "unreviewed.dfu"];
            Assert.Equal(rejected, catalog.RejectedFiles);
            Assert.Empty(FirmwareCatalog.Scan(Path.Combine(directory, "missing")).Images);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FirmwareCheckAndCalibrationBackupNeverSendCommandsOrCreateAnUpdate()
    {
        using var fake = new FakeFirmware();
        Assert.Contains("016", await fake.Updater.CheckFirmwareAsync(CancellationToken.None), StringComparison.Ordinal);
        var backup = await fake.Updater.BackupCalibrationAsync(CancellationToken.None);
        Assert.Equal(FakeFirmware.InitialCalibration, backup.Saved);
        Assert.Equal(backup.Saved, backup.LastConfirmed);
        Assert.True(backup.IsManualBackup);
        Assert.Equal("PE001600", backup.SerialNumber);
        Assert.Equal(0, fake.Commands);
        Assert.Equal(0, fake.Programs);
        Assert.Null(await fake.Updater.ReadPendingAsync(CancellationToken.None));
        await fake.Flash(await fake.Prepare());
        await Assert.ThrowsAsync<IOException>(() => fake.Updater.BackupCalibrationAsync(CancellationToken.None));
    }

    [Fact]
    public void ParserRejectsCorruptionWrongTargetsAndUnreviewedButWellFormedImages()
    {
        Assert.Throws<InvalidDataException>(() => PedalFirmwareImage.Validate([]));
        var image = SyntheticImage();
        Assert.Contains("not been reviewed", Assert.Throws<InvalidDataException>(() => PedalFirmwareImage.Validate(image)).Message, StringComparison.Ordinal);
        image[293] ^= 1;
        Assert.Contains("checksum", Assert.Throws<InvalidDataException>(() => PedalFirmwareImage.Validate(image)).Message, StringComparison.Ordinal);
        image = SyntheticImage(); image[^12] = 0;
        Assert.Contains("does not target", Assert.Throws<InvalidDataException>(() => PedalFirmwareImage.Validate(image)).Message, StringComparison.Ordinal);
        image = SyntheticImage(); BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(285), 0x08000000); SetCrc(image);
        Assert.Contains("region", Assert.Throws<InvalidDataException>(() => PedalFirmwareImage.Validate(image)).Message, StringComparison.Ordinal);
        image = SyntheticImage(); image[17] = 1; SetCrc(image);
        Assert.Contains("layout", Assert.Throws<InvalidDataException>(() => PedalFirmwareImage.Validate(image)).Message, StringComparison.Ordinal);
        image = SyntheticImage(); image[6] ^= 1;
        Assert.Contains("header", Assert.Throws<InvalidDataException>(() => PedalFirmwareImage.Validate(image)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "Upgrade successful !\r\nVerify successful !\r\n", true)]
    [InlineData(1, "Upgrade successful !\nVerify successful !", false)]
    [InlineData(0, "Upgrade successful !", false)]
    [InlineData(0, "Upgrade successful !\nVerify successful, but data not matching...", false)]
    [InlineData(0, "not Upgrade successful !\nVerify successful !", false)]
    public void ToolRequiresExactProgrammingAndVerificationSuccess(int code, string output, bool expected)
        => Assert.Equal(expected, FirmwareUpdater.VerifyToolResult(new(code, output, "")));

    [Fact]
    public void NativeToolArgumentsMatchVendorDownloadSequenceForAutoRestartingPedals()
    {
        var start = WindowsFirmwareServices.CreateProgrammingStart(@"C:\ST tools\DfuSeCommand.exe", @"C:\private images\PE0017.dfu");
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardError && start.RedirectStandardOutput && start.RedirectStandardInput && start.CreateNoWindow);
        string[] expected = ["-c", "--de", "0", "--al", "0", "-d", "--fn", @"C:\private images\PE0017.dfu"];
        Assert.Equal(expected, start.ArgumentList);
    }

    [Fact]
    public async Task ConfirmedDownloadResumesOnMatchingControllerCheckWithoutClaimingReadback()
    {
        using var fake = new FakeFirmware();
        fake.Result = new(0, "Upgrade successful !\r\n", "", ReadbackRequested: false);
        var record = await fake.Flash(await fake.Prepare());
        Assert.True(record.DownloadCompleted);
        Assert.False(record.ProgramVerified);
        Assert.Contains("Byte readback was not performed", record.Log, StringComparison.Ordinal);
        Assert.False((await fake.Updater.CheckConnectionAsync(false, CancellationToken.None)).Complete);
        fake.Reconnect();
        var review = await fake.Updater.CheckConnectionAsync(false, CancellationToken.None);
        Assert.False(review.CanAccept);
        Assert.True(review.Complete);
        Assert.Contains("Byte readback was not verified", review.Detail, StringComparison.Ordinal);
        Assert.Null(await fake.Updater.ReadPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CompletionWaitsForReconnectRestoresCalibrationAndChecksWithoutAnotherFlash()
    {
        using var fake = new FakeFirmware { AutoReconnectAfterProgram = true, AfterFlashCalibration = PedalHardwareSettings.Defaults, TransientConnectFailures = 3 };
        fake.Result = new(0, "Upgrade successful !", "", ReadbackRequested: false);
        await fake.Flash(await fake.Prepare());
        var messages = new List<string>();
        var result = await fake.Updater.CompleteUpdateAsync(new InlineProgress(messages.Add), CancellationToken.None);
        Assert.True(result.Complete);
        Assert.Equal(FakeFirmware.InitialCalibration, fake.Calibration);
        Assert.Equal(1, fake.CalibrationWrites);
        Assert.Equal(1, fake.CalibrationSaves);
        Assert.Equal(1, fake.Commands);
        Assert.Equal(1, fake.Programs);
        Assert.Null(await fake.Updater.ReadPendingAsync(CancellationToken.None));
        Assert.DoesNotContain(FirmwareUpdater.ReconnectInstructions, messages);
    }

    [Fact]
    public async Task ReconnectionTimeoutRetainsInstructionsAndManualReconnectCompletesWithoutReflashing()
    {
        using var fake = new FakeFirmware();
        fake.Result = new(0, "Upgrade successful !", "", ReadbackRequested: false);
        var programmed = await fake.Flash(await fake.Prepare());
        var failure = await Assert.ThrowsAsync<IOException>(() => fake.Updater.CompleteUpdateAsync(new InlineProgress(_ => { }), CancellationToken.None));
        Assert.Equal(FirmwareUpdater.ReconnectInstructions, failure.Message);
        var pending = await fake.Updater.ReadPendingAsync(CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal("Reconnect pedals", pending.Phase);
        Assert.True(pending.DownloadCompleted);
        Assert.False(pending.ProgramVerified);
        Assert.Equal(0, fake.CalibrationWrites);
        Assert.True(File.Exists(pending.BackupPath));
        using (var log = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(pending.BackupPath)!, "update-log.json"))))
        {
            Assert.Equal("Reconnect pedals", log.RootElement.GetProperty("phase").GetString());
            Assert.Contains(FirmwareUpdater.ReconnectInstructions, log.RootElement.GetProperty("log").GetString()!, StringComparison.Ordinal);
        }
        fake.Reconnect();
        fake.Calibration = PedalHardwareSettings.Defaults;
        Assert.True((await fake.Updater.CompleteUpdateAsync(new InlineProgress(_ => { }), CancellationToken.None)).Complete);
        Assert.Equal(FakeFirmware.InitialCalibration, fake.Calibration);
        Assert.Equal(1, fake.Programs);
        Assert.Equal(1, fake.Commands);
        Assert.Equal(1, fake.CalibrationWrites);
        Assert.Equal(1, fake.CalibrationSaves);
        Assert.Null(await fake.Updater.ReadPendingAsync(CancellationToken.None));
        Assert.True(File.Exists(programmed.BackupPath));
    }

    [Fact]
    public async Task CompletionDoesNotRewriteCalibrationWhenTheBackupAlreadyMatches()
    {
        using var fake = new FakeFirmware { AutoReconnectAfterProgram = true };
        await fake.Flash(await fake.Prepare());
        Assert.True((await fake.Updater.CompleteUpdateAsync(new InlineProgress(_ => { }), CancellationToken.None)).Complete);
        Assert.Equal(0, fake.CalibrationWrites);
        Assert.Equal(0, fake.CalibrationSaves);
    }

    [Theory]
    [InlineData("port")]
    [InlineData("identity")]
    [InlineData("missing")]
    public async Task CompletionNeverRestoresAnUnexpectedOrMissingController(string failure)
    {
        using var fake = new FakeFirmware { AutoReconnectAfterProgram = failure != "missing", AfterFlashCalibration = PedalHardwareSettings.Defaults };
        await fake.Flash(await fake.Prepare());
        if (failure == "port") { fake.ConnectionPort = "port-2"; }
        if (failure == "identity") { fake.Serial = "PE0017FF"; }
        await Assert.ThrowsAsync<IOException>(() => fake.Updater.CompleteUpdateAsync(new InlineProgress(_ => { }), CancellationToken.None));
        Assert.Equal(0, fake.CalibrationWrites);
        Assert.Equal(0, fake.CalibrationSaves);
        Assert.NotNull(await fake.Updater.ReadPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedCalibrationSaveKeepsTheUpdateBlockedAndCanBeRecoveredWithoutReflashing()
    {
        using var fake = new FakeFirmware { AutoReconnectAfterProgram = true, AfterFlashCalibration = PedalHardwareSettings.Defaults, FailCalibrationSave = true };
        await fake.Flash(await fake.Prepare());
        var failure = await Assert.ThrowsAsync<IOException>(() => fake.Updater.CompleteUpdateAsync(new InlineProgress(_ => { }), CancellationToken.None));
        var pending = await fake.Updater.ReadPendingAsync(CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal("Controller check needs attention", pending.Phase);
        Assert.Contains(failure.Message, pending.Log, StringComparison.Ordinal);
        Assert.False((await fake.Updater.CheckConnectionAsync(false, CancellationToken.None)).Complete);
        fake.FailCalibrationSave = false;
        Assert.True((await fake.Updater.CompleteUpdateAsync(new InlineProgress(_ => { }), CancellationToken.None)).Complete);
        Assert.Equal(1, fake.Programs);
        Assert.Equal(2, fake.CalibrationSaves);
    }

    [Fact]
    public async Task UnconfirmedProgrammingNeverTriggersAutomaticCalibrationRestoration()
    {
        using var fake = new FakeFirmware { AutoReconnectAfterProgram = true, AfterFlashCalibration = PedalHardwareSettings.Defaults };
        fake.Result = new(1, "", "unknown result");
        var plan = await fake.Prepare();
        await Assert.ThrowsAsync<IOException>(() => fake.Flash(plan));
        await Assert.ThrowsAsync<IOException>(() => fake.Updater.CompleteUpdateAsync(new InlineProgress(_ => { }), CancellationToken.None));
        Assert.Equal(0, fake.CalibrationWrites);
        Assert.Equal(0, fake.CalibrationSaves);
    }

    [Fact]
    public async Task ControllerCheckCannotClearTheUpdateAtAnotherUsbPort()
    {
        using var fake = new FakeFirmware();
        await fake.Flash(await fake.Prepare()); fake.Reconnect(); fake.ConnectionPort = "port-2";
        var result = await fake.Updater.CheckConnectionAsync(false, CancellationToken.None);
        Assert.False(result.Complete || result.CanAccept);
        Assert.NotNull(await fake.Updater.ReadPendingAsync(CancellationToken.None));
        Assert.False((await fake.Updater.CheckConnectionAsync(true, CancellationToken.None)).Complete);
        fake.ConnectionPort = "port-1";
        Assert.True((await fake.Updater.CheckConnectionAsync(false, CancellationToken.None)).Complete);
    }

    [Theory]
    [InlineData(1, "Upgrade successful !")]
    [InlineData(0, "not Upgrade successful !")]
    [InlineData(0, "Download failed")]
    public async Task DownloadOnlyModeStillRejectsMissingOrFailedProgramming(int exitCode, string output)
    {
        using var fake = new FakeFirmware();
        fake.Result = new(exitCode, output, "", ReadbackRequested: false);
        var plan = await fake.Prepare();
        await Assert.ThrowsAsync<IOException>(() => fake.Flash(plan));
        var record = await fake.Updater.ReadPendingAsync(CancellationToken.None);
        Assert.False(record!.DownloadCompleted);
        Assert.False(record.ProgramVerified);
    }

    [Fact]
    public async Task CalibrationResetIsReportedAndBackupCanBeExplicitlyRestoredWithoutAnotherFlash()
    {
        using var fake = new FakeFirmware();
        fake.Result = new(0, "Upgrade successful !", "", ReadbackRequested: false);
        await fake.Flash(await fake.Prepare());
        fake.Reconnect();
        fake.Calibration = new(new(0, 16384), new(0, 16777216), new(0, 16384));
        var review = await fake.Updater.CheckConnectionAsync(false, CancellationToken.None);
        Assert.False(review.Complete);
        Assert.False(review.CanAccept);
        Assert.Contains("calibration does not match", review.Detail, StringComparison.Ordinal);
        Assert.False((await fake.Updater.CheckConnectionAsync(true, CancellationToken.None)).Complete);
        await Assert.ThrowsAsync<InvalidOperationException>(fake.Prepare);
        await fake.Updater.RestoreCalibrationBackupAsync(CancellationToken.None);
        Assert.Equal(FakeFirmware.InitialCalibration, fake.Calibration);
        Assert.Equal(1, fake.CalibrationWrites);
        Assert.Equal(1, fake.CalibrationSaves);
        Assert.Equal(1, fake.Programs);
        Assert.Equal(1, fake.Commands);
        Assert.NotNull(await fake.Updater.ReadPendingAsync(CancellationToken.None));
        Assert.True((await fake.Updater.CheckConnectionAsync(false, CancellationToken.None)).Complete);
        var next = await fake.Prepare();
        Assert.Equal(FakeFirmware.InitialCalibration, next.Calibration);
    }

    [Fact]
    public async Task CalibrationBackupRestorationRejectsAnotherControllerBeforeWriting()
    {
        using var fake = new FakeFirmware();
        await fake.Flash(await fake.Prepare());
        fake.Reconnect(); fake.Serial = "PE0017FF";
        await Assert.ThrowsAsync<IOException>(() => fake.Updater.RestoreCalibrationBackupAsync(CancellationToken.None));
        Assert.Equal(0, fake.CalibrationWrites);
        Assert.Equal(0, fake.CalibrationSaves);
    }

    [Fact]
    public void StagedImageLeaseAllowsStExclusiveReadButPreventsReplacementAndWrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-image-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "image.dfu");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            using var lease = FirmwareImageLease.Open(path);
            // Reopen a protected staged image as a retry would after process termination.
            using (var retry = FirmwareImageLease.Open(path))
            { using var reader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); Assert.Equal(1, reader.ReadByte()); }
            using (var stReader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { Assert.Equal(1, stReader.ReadByte()); }
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllBytes(path, [4, 5]));
            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(path));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public async Task PreparationIsReadOnlyAndSuccessfulFlashRequiresTheSameControllerAfterReconnect()
    {
        using var fake = new FakeFirmware();
        var plan = await fake.Prepare();
        Assert.Equal(0, fake.Commands);
        Assert.False(File.Exists(fake.Updater.JournalPath));
        var progress = new List<string>();
        var record = await fake.Updater.FlashAsync(plan, new InlineProgress(progress.Add), CancellationToken.None);
        Assert.Equal(1, fake.Commands);
        Assert.Equal(1, fake.Programs);
        Assert.True(fake.JournalBeforeBoot);
        Assert.True(record.ProgramVerified);
        var backup = await HardwareRecoveryStore.ReadAsync(record.BackupPath);
        Assert.True(backup.IsManualBackup);
        Assert.Equal(FakeFirmware.InitialCalibration, backup.Saved);
        Assert.False((await fake.Updater.CheckConnectionAsync(false, CancellationToken.None)).Complete);
        fake.Reconnect();
        fake.Serial = "PE0017AA";
        await Assert.ThrowsAsync<IOException>(() => fake.Updater.CheckConnectionAsync(false, CancellationToken.None));
        fake.Serial = "PE001700";
        var complete = await fake.Updater.CheckConnectionAsync(false, CancellationToken.None);
        Assert.True(complete.Complete);
        Assert.Null(await fake.Updater.ReadPendingAsync(CancellationToken.None));
        Assert.True(File.Exists(record.BackupPath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(record.BackupPath)!, "update-log.json")));
        Assert.Contains(progress, message => message.StartsWith("Programming verified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingToolChangedImageChangedControllerAndDowngradesFailBeforeBootloader()
    {
        using var fake = new FakeFirmware();
        fake.ToolFailure = true;
        await Assert.ThrowsAsync<IOException>(fake.Prepare);
        fake.ToolFailure = false;
        fake.Serial = "PE001800";
        await Assert.ThrowsAsync<IOException>(fake.Prepare);
        fake.Serial = "PE001600";
        var plan = await fake.Prepare();
        File.WriteAllBytes(fake.ImagePath, [7, 8]);
        await Assert.ThrowsAsync<IOException>(() => fake.Flash(plan));
        File.WriteAllBytes(fake.ImagePath, FakeFirmware.Bytes);
        fake.Serial = "PE0016AA";
        await Assert.ThrowsAsync<IOException>(() => fake.Flash(plan));
        fake.Serial = "PE001600";
        File.WriteAllText(fake.ToolPath, "changed tool");
        await Assert.ThrowsAsync<IOException>(() => fake.Flash(plan));
        Assert.Equal(0, fake.Commands);
        Assert.False(File.Exists(fake.Updater.JournalPath));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-port")]
    [InlineData("multiple")]
    [InlineData("no-driver")]
    public async Task BootloaderFailuresRetainRecoveryAndNeverStartProgramming(string failure)
    {
        using var fake = new FakeFirmware();
        var plan = await fake.Prepare();
        fake.BootFailure = failure;
        await Assert.ThrowsAnyAsync<Exception>(() => fake.Flash(plan));
        Assert.Equal(1, fake.Commands);
        Assert.Equal(0, fake.Programs);
        Assert.NotNull(await fake.Updater.ReadPendingAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(fake.Prepare);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    public async Task DisconnectDuringBootCommandContinuesOnlyAfterMatchingBootloaderAppears(string error)
    {
        using var fake = new FakeFirmware();
        var plan = await fake.Prepare();
        fake.BootWriteFailure = error switch
        {
            "timeout" => new TimeoutException("Boot write timed out"),
            "cancelled" => new OperationCanceledException("Boot write cancelled"),
            _ => new IOException("Cannot write HID feature report (Win32 31)")
        };
        var result = await fake.Flash(plan);
        Assert.True(result.ProgramVerified);
        Assert.Contains("Bootloader command completion uncertain:", result.Log, StringComparison.Ordinal);
        Assert.Contains("Verify successful !", result.Log, StringComparison.Ordinal);
        Assert.True(fake.JournalBeforeBoot);
        Assert.Equal(1, fake.Commands);
        Assert.Equal(1, fake.Programs);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-port")]
    [InlineData("multiple")]
    [InlineData("no-driver")]
    public async Task UncertainBootWriteNeverBypassesPortOrDriverChecks(string failure)
    {
        using var fake = new FakeFirmware();
        var plan = await fake.Prepare();
        fake.BootFailure = failure;
        fake.BootWriteFailure = new IOException("Write completion uncertain");
        await Assert.ThrowsAsync<IOException>(() => fake.Flash(plan));
        Assert.Equal(0, fake.Programs);
        Assert.NotNull(await fake.Updater.ReadPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BootloaderDriverIsPreparedOnceAndIdentityIsCheckedAgainBeforeProgramming()
    {
        using var fake = new FakeFirmware();
        var plan = await fake.Prepare();
        fake.BootFailure = "no-driver";
        fake.RepairDriver = true;
        Assert.True((await fake.Flash(plan)).ProgramVerified);
        Assert.Equal(1, fake.DriverPreparations);
        fake.Result = new(1, "", "retry required");
        fake.BootFailure = "no-driver";
        fake.DriverRepairChangesPort = true;
        await Assert.ThrowsAsync<IOException>(() => fake.Updater.RetryAsync(new InlineProgress(_ => { }), CancellationToken.None));
        Assert.Equal(2, fake.DriverPreparations);
        Assert.Equal(1, fake.Programs);
    }

    [Fact]
    public async Task RetryRepairsTheRecordedBootloaderWithoutSendingAnotherBootCommand()
    {
        using var fake = new FakeFirmware();
        var plan = await fake.Prepare();
        fake.BootFailure = "no-driver";
        await Assert.ThrowsAsync<IOException>(() => fake.Flash(plan));
        Assert.Equal(1, fake.DriverPreparations);
        fake.RepairDriver = true;
        var result = await fake.Updater.RetryAsync(new InlineProgress(_ => { }), CancellationToken.None);
        Assert.True(result.ProgramVerified);
        Assert.Equal(1, fake.Commands);
        Assert.Equal(1, fake.Programs);
        Assert.Equal(2, fake.DriverPreparations);
    }

    [Fact]
    public async Task ExistingDfuDeviceIsRejectedBeforePreparationOrBootCommand()
    {
        using var fake = new FakeFirmware();
        fake.OtherDfu = true;
        await Assert.ThrowsAsync<IOException>(fake.Prepare);
        fake.OtherDfu = false;
        var plan = await fake.Prepare();
        fake.OtherDfu = true;
        await Assert.ThrowsAsync<IOException>(() => fake.Flash(plan));
        Assert.Equal(0, fake.Commands);
    }

    [Fact]
    public async Task FailedProgrammingCanRetryOnlyWithOriginalStagedImageToolAndUsbPort()
    {
        using var fake = new FakeFirmware();
        var plan = await fake.Prepare();
        fake.Result = new(0, "Upgrade successful !", "verification failed");
        await Assert.ThrowsAsync<IOException>(() => fake.Flash(plan));
        var record = (await fake.Updater.ReadPendingAsync(CancellationToken.None))!;
        Assert.False(record.ProgramVerified);
        Assert.Contains("verification failed", record.Log, StringComparison.Ordinal);
        fake.BootFailure = "wrong-port";
        await Assert.ThrowsAsync<IOException>(() => fake.Updater.RetryAsync(new InlineProgress(_ => { }), CancellationToken.None));
        fake.BootFailure = "";
        File.WriteAllBytes(record.StagedImagePath, [5, 6]);
        await Assert.ThrowsAsync<IOException>(() => fake.Updater.RetryAsync(new InlineProgress(_ => { }), CancellationToken.None));
        Assert.Equal(1, fake.Programs);
        File.WriteAllBytes(record.StagedImagePath, FakeFirmware.Bytes);
        fake.Result = FakeFirmware.Success;
        var retry = await fake.Updater.RetryAsync(new InlineProgress(_ => { }), CancellationToken.None);
        Assert.True(retry.ProgramVerified);
        Assert.Equal(1, fake.Commands);
        Assert.Equal(2, fake.Programs);
    }

    [Fact]
    public async Task ChangedCalibrationAndInterruptedVerificationRequireExplicitReview()
    {
        using var fake = new FakeFirmware();
        var plan = await fake.Prepare();
        await fake.Flash(plan); fake.Reconnect();
        fake.Calibration = new(new(100, 2000), new(200, 20000), new(100, 2000));
        var changed = await fake.Updater.CheckConnectionAsync(false, CancellationToken.None);
        Assert.False(changed.CanAccept || changed.Complete);
        Assert.True(File.Exists(fake.Updater.JournalPath));
        Assert.False((await fake.Updater.CheckConnectionAsync(true, CancellationToken.None)).Complete);
        await fake.Updater.RestoreCalibrationBackupAsync(CancellationToken.None);
        Assert.True((await fake.Updater.CheckConnectionAsync(false, CancellationToken.None)).Complete);

        fake.Serial = "PE001600";
        plan = await fake.Prepare(); fake.Result = new(1, "", "tool failed");
        await Assert.ThrowsAsync<IOException>(() => fake.Flash(plan)); fake.Reconnect();
        var interrupted = await fake.Updater.CheckConnectionAsync(false, CancellationToken.None);
        Assert.True(interrupted.CanAccept && !interrupted.Complete);
        Assert.True((await fake.Updater.CheckConnectionAsync(true, CancellationToken.None)).Complete);
    }

    [Fact]
    public async Task CancellationAfterBootloaderDoesNotCancelOrTerminateProgramming()
    {
        using var fake = new FakeFirmware();
        var plan = await fake.Prepare();
        var barrier = new TaskCompletionSource<FirmwareToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ProgramBarrier = barrier.Task;
        using var cancel = new CancellationTokenSource();
        var pending = fake.Updater.FlashAsync(plan, new InlineProgress(_ => { }), cancel.Token);
        for (var attempt = 0; fake.Programs == 0 && attempt < 200; attempt++) { await Task.Delay(5); }
        Assert.Equal(1, fake.Programs);
        cancel.Cancel();
        Assert.False(pending.IsCompleted);
        Assert.True(File.Exists(fake.Updater.JournalPath));
        barrier.SetResult(FakeFirmware.Success);
        Assert.True((await pending).ProgramVerified);
    }

    [Fact]
    public async Task CorruptOrInvalidJournalPathsStayBlockedRatherThanDisappearing()
    {
        using var fake = new FakeFirmware();
        Directory.CreateDirectory(fake.Updater.DirectoryPath);
        File.WriteAllText(fake.Updater.JournalPath, "{");
        await Assert.ThrowsAsync<InvalidDataException>(() => fake.Updater.ReadPendingAsync(CancellationToken.None));
        Assert.Equal(0, fake.Commands);
        File.Delete(fake.Updater.JournalPath); Directory.CreateDirectory(fake.Updater.JournalPath);
        await Assert.ThrowsAnyAsync<Exception>(() => fake.Updater.ReadPendingAsync(CancellationToken.None));
    }

    private static byte[] SyntheticImage()
    {
        var bytes = new byte[325]; "DfuSe"u8.CopyTo(bytes); bytes[5] = 1; bytes[10] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6), (uint)bytes.Length - 16);
        "Target"u8.CopyTo(bytes.AsSpan(11));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(277), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(281), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(285), 0x0800C000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(289), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 14), PedalInputReport.ProductId);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 12), PedalInputReport.VendorId);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 10), 0x011A);
        "UFD"u8.CopyTo(bytes.AsSpan(bytes.Length - 8)); bytes[^5] = 16; SetCrc(bytes); return bytes;
    }
    private static void SetCrc(byte[] bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes.AsSpan(0, bytes.Length - 4))
        { crc ^= value; for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); } }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), crc);
    }

    internal sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    internal sealed class FakeFirmware : IDisposable, IFeatureReportReader, IFeatureReportWriter
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-firmware-tests-" + Guid.NewGuid().ToString("N"));
        internal static readonly byte[] Bytes = [1, 2, 3, 4];
        internal static readonly PedalCalibration InitialCalibration = new(new(400, 8000), new(3_400_000, 4_500_000), new(400, 8000));
        internal static readonly FirmwareToolResult Success = new(0, "Upgrade successful !\r\nVerify successful !\r\n", "");
        internal string Serial { get; set; } = "PE001600";
        internal string ConnectionPort { get; set; } = "port-1";
        internal PedalCalibration Calibration { get; set; } = InitialCalibration;
        internal bool ToolFailure { get; set; }
        internal bool OtherDfu { get; set; }
        internal bool InBootloader { get; set; }
        internal string BootFailure { get; set; } = "";
        internal int Commands { get; private set; }
        internal int Programs { get; private set; }
        internal bool JournalBeforeBoot { get; private set; }
        internal Exception? BootWriteFailure { get; set; }
        internal bool RepairDriver { get; set; }
        internal bool DriverRepairChangesPort { get; set; }
        internal int DriverPreparations { get; private set; }
        internal int CalibrationWrites { get; private set; }
        internal int CalibrationSaves { get; private set; }
        internal Task<FirmwareToolResult>? ProgramBarrier { get; set; }
        internal bool AutoReconnectAfterProgram { get; set; }
        internal PedalCalibration? AfterFlashCalibration { get; set; }
        internal bool FailCalibrationSave { get; set; }
        internal int TransientConnectFailures { get; set; }
        internal FirmwareToolResult Result { get; set; } = Success;
        internal string ImagePath => Path.Combine(directory, "image.dfu");
        internal string ToolPath => Path.Combine(directory, "DfuSeCommand.exe");
        internal FirmwareUpdater Updater { get; }
        private static readonly HidDeviceInfo Device = new("fake-pedal", PedalInputReport.VendorId, PedalInputReport.ProductId, 0x200, 0xFF00, 1, 0, 0, 17);
        internal FakeFirmware()
        {
            Directory.CreateDirectory(directory); File.WriteAllBytes(ImagePath, Bytes); File.WriteAllText(ToolPath, "test tool");
            Updater = new(Path.Combine(directory, "updates"), new(
                _ =>
                {
                    if (Programs > 0 && !InBootloader && TransientConnectFailures > 0)
                    { TransientConnectFailures--; return Task.FromException<FirmwareConnection>(new IOException("USB still reconnecting")); }
                    return Task.FromResult(new FirmwareConnection(new(Device, Serial, this, this), new("usb-pedals", ConnectionPort), Calibration));
                },
                () => OtherDfu ? [new("other", "port-2")] : !InBootloader || BootFailure == "missing" ? []
                    : BootFailure == "multiple" ? [new("boot", "port-1"), new("other", "port-2")]
                    : [new("boot", BootFailure == "wrong-port" ? "port-2" : "port-1", BootFailure != "no-driver")],
                (_, _) => ToolFailure ? Task.FromException(new IOException("Missing ST tool")) : Task.CompletedTask,
                async (_, _, _) =>
                {
                    Programs++;
                    var result = await (ProgramBarrier ?? Task.FromResult(Result));
                    if (AutoReconnectAfterProgram) { Reconnect(); Calibration = AfterFlashCalibration ?? Calibration; }
                    return result;
                },
                (_, _) => Task.CompletedTask,
                bytes => new("017", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, 0x0800C000, bytes.Length),
                device =>
                {
                    Assert.Equal("port-1", device.LocationPath);
                    DriverPreparations++;
                    if (RepairDriver) { BootFailure = DriverRepairChangesPort ? "wrong-port" : ""; }
                    return Task.CompletedTask;
                }));
        }
        internal Task<FirmwarePlan> Prepare() => Updater.PrepareAsync(ImagePath, ToolPath, CancellationToken.None);
        internal Task<FirmwareUpdateRecord> Flash(FirmwarePlan plan) => Updater.FlashAsync(plan, new InlineProgress(_ => { }), CancellationToken.None);
        internal void Reconnect() { InBootloader = false; Serial = "PE001700"; }
        public Task<byte[]> ReadAsync(HidDeviceInfo device, byte id, CancellationToken token) => Task.FromResult(PedalCalibrationReport.Encode(Calibration));
        public Task WriteAsync(HidDeviceInfo device, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            if (bytes.Span[0] == PedalCalibrationReport.ReportId)
            { Calibration = PedalCalibrationReport.Decode(bytes.Span); CalibrationWrites++; return Task.CompletedTask; }
            if (bytes.Span.SequenceEqual(PedalCommandReport.Encode(PedalDeviceCommand.SaveConfiguration)))
            { CalibrationSaves++; return FailCalibrationSave ? Task.FromException(new IOException("Save completion uncertain")) : Task.CompletedTask; }
            Assert.Equal(PedalCommandReport.Encode(PedalDeviceCommand.EnterBootloader), bytes.ToArray());
            Commands++; JournalBeforeBoot = File.Exists(Updater.JournalPath); InBootloader = true;
            return BootWriteFailure is not null ? Task.FromException(BootWriteFailure) : Task.CompletedTask;
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
}

[Collection("WPF UI")]
public sealed class FirmwareUpdateUiTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task ReplugInstructionsSurviveRestartAndCheckFirmwareFinishesWithoutAnotherFlash(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var fake = new FirmwareUpdateTests.FakeFirmware();
        fake.Result = new(0, "Upgrade successful !", "", ReadbackRequested: false);
        await fake.Flash(await fake.Prepare());
        await Assert.ThrowsAsync<IOException>(() => fake.Updater.CompleteUpdateAsync(new Progress<string>(), CancellationToken.None));
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(new(), desktopServices: desktop.Services with { Firmware = fake.Updater },
            profileLibrary: new(Path.Combine(fake.Updater.DirectoryPath, "profiles")));
        Localizer.Instance.Language = language;
        await main.CheckFirmwareStartupAsync();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        Assert.True(model.Firmware.Blocked);
        Assert.Equal(FirmwareUpdater.ReconnectInstructions, model.Firmware.Status);
        var checkedFirmware = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new FirmwareUpdateWindow(model.Firmware, main.Resources,
            () => throw new InvalidOperationException("The reconnect check must not flash firmware."),
            review: async () => { await main.CheckFirmwareAsync(); checkedFirmware.SetResult(); })
        { Opacity = 0, ShowInTaskbar = false };
        try
        {
            dialog.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var message = Assert.IsType<TextBlock>(dialog.FindName("FirmwareStatusLabel"));
            Assert.Equal(Localizer.Translate(FirmwareUpdater.ReconnectInstructions), message.Text);
            Assert.Contains("5", message.Text, StringComparison.Ordinal);
            Assert.Contains("USB", message.Text, StringComparison.Ordinal);
            var check = Assert.IsType<Button>(dialog.FindName("FlashFirmwareButton"));
            Assert.True(check.IsEnabled);
            Assert.True(model.Firmware.ProgrammingCompleted);
            Assert.Equal(Localizer.Translate("Check firmware"), check.Content);
            Assert.False(Assert.IsType<Button>(dialog.FindName("ReviewFirmwareButton")).IsVisible);
            dialog.UpdateLayout();
            UiRefinementTests.CapturePreview(Assert.IsAssignableFrom<FrameworkElement>(dialog.Content), $"firmware-reconnect-{language}.png");
            fake.Reconnect();
            fake.Calibration = PedalHardwareSettings.Defaults;
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await checkedFirmware.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(model.Firmware.Completed);
            Assert.False(model.Firmware.Blocked);
            Assert.Equal(1, fake.Programs);
            Assert.Equal(1, fake.Commands);
            Assert.Equal(FirmwareUpdateTests.FakeFirmware.InitialCalibration, fake.Calibration);
        }
        finally { dialog.Close(); Localizer.Instance.Language = "en"; }
    });

    [Fact]
    public Task UnconfirmedProgrammingHasAnInlineRecoveryActionWithoutAModalConfirmation() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var fake = new FirmwareUpdateTests.FakeFirmware();
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(new(), desktopServices: desktop.Services with { Firmware = fake.Updater });
        fake.Result = new(1, "", "programming result unknown");
        await main.UpdateFirmwareAsync(fake.ImagePath); fake.Reconnect();
        await main.CheckFirmwareAsync();
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        Assert.True(model.Firmware.Blocked && model.Firmware.CanUseDetected);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new FirmwareUpdateWindow(model.Firmware, main.Resources, () => Task.CompletedTask,
            accept: async () => { await main.CheckUpdatedFirmwareAsync(true); accepted.SetResult(); })
        { Opacity = 0, ShowInTaskbar = false };
        try
        {
            dialog.Show(); dialog.UpdateLayout();
            var button = Assert.IsType<Button>(dialog.FindName("UseDetectedControllerButton"));
            Assert.True(button.IsVisible && button.IsEnabled);
            model.Firmware.Running = true;
            Assert.False(button.IsEnabled);
            model.Firmware.Running = false;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(model.Firmware.Blocked);
            Assert.Contains("Byte readback was not verified", model.Firmware.Status, StringComparison.Ordinal);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task FirmwareBackupRejectsTemporaryBoundsWithPendingHardwareRecovery() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var fake = new FirmwareUpdateTests.FakeFirmware();
        var plan = await fake.Prepare();
        var folder = Path.Combine(fake.Updater.DirectoryPath, "raw-recovery");
        var snapshot = new HardwareRecoverySnapshot(1, DateTimeOffset.UtcNow, plan.Device, plan.Calibration,
            PedalHardwareSettings.Defaults, null, SerialNumber: plan.SerialNumber);
        await new HardwareRecoveryStore(Path.Combine(folder, "raw.json")).WriteAsync(snapshot, CancellationToken.None);
        fake.Calibration = PedalHardwareSettings.Defaults;
        var recovery = new StartupHardwareRecovery(folder, (_, _) => Task.FromResult(new RecoveryControllerConnection(plan.Device, plan.SerialNumber, fake, fake)));
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(new(), desktopServices: desktop.Services with { Firmware = fake.Updater, Recovery = recovery });
        var destination = Path.Combine(fake.Updater.DirectoryPath, "backup.json");
        await main.BackupFirmwareCalibrationAsync(destination);
        Assert.False(File.Exists(destination));
        Assert.Equal(0, fake.CalibrationWrites);
        Assert.Equal(0, fake.Commands);
        Assert.Contains("Temporary raw-input bounds", Assert.IsType<DashboardViewModel>(main.DataContext).Firmware.Status, StringComparison.Ordinal);
    });

    [Fact]
    public Task FirmwareDialogAutomaticallyRestoresChecksAndFinishesWithOneOkButton() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var fake = new FirmwareUpdateTests.FakeFirmware { AutoReconnectAfterProgram = true, AfterFlashCalibration = PedalHardwareSettings.Defaults };
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(new(), desktopServices: desktop.Services with { Firmware = fake.Updater },
            profileLibrary: new(Path.Combine(fake.Updater.DirectoryPath, "profiles")));
        fake.Result = new(0, "Upgrade successful !", "", ReadbackRequested: false);
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        model.Firmware.SelectedImage = new(fake.ImagePath, "017", "test.dfu");
        var flashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new FirmwareUpdateWindow(model.Firmware, main.Resources,
            async () => { await main.UpdateFirmwareAsync(fake.ImagePath); flashed.SetResult(); })
        { Opacity = 0, ShowInTaskbar = false };
        try
        {
            dialog.Show(); dialog.UpdateLayout();
            var flash = Assert.IsType<Button>(dialog.FindName("FlashFirmwareButton"));
            flash.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await flashed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal(FirmwareUpdateTests.FakeFirmware.InitialCalibration, fake.Calibration);
            Assert.Equal(1, fake.Programs);
            Assert.False(model.Firmware.Blocked);
            Assert.False(model.Firmware.CanAccept);
            Assert.True(model.Firmware.Completed);
            Assert.Equal("OK", flash.Content);
            Assert.Contains("Pedals are ready", model.Firmware.Status, StringComparison.Ordinal);
            Assert.Equal(Color.FromRgb(0x78, 0xE3, 0xC6), Assert.IsType<SolidColorBrush>(Assert.IsType<TextBlock>(dialog.FindName("FirmwareStatusLabel")).Foreground).Color);
            Assert.False(Assert.IsType<Button>(dialog.FindName("RestoreFirmwareCalibrationButton")).IsVisible);
            Assert.False(Assert.IsType<Button>(dialog.FindName("ReviewFirmwareButton")).IsVisible);
            dialog.UpdateLayout();
            UiRefinementTests.CapturePreview(Assert.IsAssignableFrom<FrameworkElement>(dialog.Content), "firmware-complete.png");
            flash.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(dialog.IsVisible);
            Assert.Equal(1, fake.Programs);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task FirmwareDialogRunsPreflightAndFlashAndStartupPendingUpdateBlocksLiveInput() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var fake = new FirmwareUpdateTests.FakeFirmware();
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using (var main = new MainWindow(new(), desktopServices: desktop.Services with { Firmware = fake.Updater }, profileLibrary: new(Path.Combine(fake.Updater.DirectoryPath, "profiles"))))
        {
            await main.RequestPanelModeAsync(SidePanelMode.Settings);
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            await main.InspectFirmwareAsync();
            Assert.Contains("016", model.Firmware.Status, StringComparison.Ordinal);
            var backupPath = Path.Combine(fake.Updater.DirectoryPath, "manual-backup.json");
            await main.BackupFirmwareCalibrationAsync(backupPath);
            Assert.Equal(FirmwareUpdateTests.FakeFirmware.InitialCalibration, (await HardwareRecoveryStore.ReadAsync(backupPath)).Saved);
            Assert.Equal(0, fake.Commands);
            var content = Assert.IsAssignableFrom<FrameworkElement>(main.Content);
            content.Measure(new(1340, 900)); content.Arrange(new(0, 0, 1340, 900)); content.UpdateLayout();
            Assert.True(Assert.IsType<Button>(main.FindName("UpdateFirmwareButton")).IsEnabled);
            Assert.NotNull(main.FindName("CheckFirmwareButton"));
            Assert.NotNull(main.FindName("BackupFirmwareCalibrationButton"));
            model.Firmware.Images = [new(fake.ImagePath, "017", "test.dfu")];
            model.Firmware.SelectedImage = model.Firmware.Images[0];
            var dialog = new FirmwareUpdateWindow(model.Firmware, main.Resources, () => main.UpdateFirmwareAsync(fake.ImagePath)) { Opacity = 0, ShowInTaskbar = false };
            dialog.Show();
            dialog.UpdateLayout();
            Assert.True(Assert.IsType<Button>(dialog.FindName("FlashFirmwareButton")).IsEnabled);
            try
            {
                string[] languages = ["en", "uk"];
                foreach (var language in languages)
                {
                    Localizer.Instance.Language = language;
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                    dialog.UpdateLayout();
                    Assert.Equal(Localizer.Translate("Update firmware"), dialog.Title);
                    Assert.Equal(Localizer.Translate("Flash"), Assert.IsType<Button>(dialog.FindName("FlashFirmwareButton")).Content);
                    if (Environment.GetEnvironmentVariable("SHERPAAXIS_UI_CAPTURE_DIR") is { Length: > 0 } folder)
                    {
                        var root = Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetChild(dialog, 0));
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        Directory.CreateDirectory(folder);
                        using var file = File.Create(Path.Combine(folder, $"firmware-dialog-{language}.png")); encoder.Save(file);
                    }
                }
            }
            finally { Localizer.Instance.Language = "en"; }
            fake.ToolFailure = true;
            await main.UpdateFirmwareAsync(fake.ImagePath);
            Assert.Equal(0, fake.Commands);
            Assert.False(model.Firmware.Blocked || model.Firmware.Running);
            fake.ToolFailure = false;
            var barrier = new TaskCompletionSource<FirmwareToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.ProgramBarrier = barrier.Task;
            var flashing = main.UpdateFirmwareAsync(fake.ImagePath);
            for (var attempt = 0; fake.Programs == 0 && attempt < 200; attempt++) { await Task.Delay(5); }
            Assert.Equal(1, fake.Programs);
            Assert.True(model.Firmware.Running);
            Assert.False(Assert.IsType<Button>(dialog.FindName("FlashFirmwareButton")).IsEnabled);
            dialog.Close();
            Assert.True(dialog.IsVisible);
            barrier.SetResult(FirmwareUpdateTests.FakeFirmware.Success);
            await flashing;
            Assert.True(model.Firmware.Blocked);
            Assert.Equal("Check firmware", model.Firmware.FlashLabel);
            Assert.False(model.Firmware.Running);
            dialog.Close();
        }
        var inputStarts = 0;
        await using (var restarted = new MainWindow(new(), desktopServices: desktop.Services with
        {
            Firmware = fake.Updater,
            Runtime = new(token => { inputStarts++; throw new InvalidOperationException("Input must stay blocked"); }, () => throw new InvalidOperationException("Output must stay blocked"))
        }, profileLibrary: new(Path.Combine(fake.Updater.DirectoryPath, "profiles"))))
        {
            await restarted.InitializeForLaunchAsync();
            var model = Assert.IsType<DashboardViewModel>(restarted.DataContext);
            Assert.True(model.Firmware.Blocked && model.IsSettingsMode);
            Assert.False(restarted.ShouldStartHidden);
            Assert.Equal(0, inputStarts);
            model.Firmware.Running = true;
            restarted.Close();
            Assert.False(restarted.IsDisposed);
            model.Firmware.Running = false;
        }
    });
}
