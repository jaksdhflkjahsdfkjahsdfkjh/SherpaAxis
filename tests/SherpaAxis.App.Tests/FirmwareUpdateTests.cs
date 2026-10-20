using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
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
    public void NativeToolArgumentsKeepPathsSeparateAndEnableReadbackVerification()
    {
        var start = WindowsFirmwareServices.CreateProgrammingStart(@"C:\ST tools\DfuSeCommand.exe", @"C:\private images\PE0017.dfu");
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardError && start.RedirectStandardOutput && start.RedirectStandardInput && start.CreateNoWindow);
        string[] expected = ["-c", "--de", "0", "--al", "0", "-d", "--v", "--fn", @"C:\private images\PE0017.dfu"];
        Assert.Equal(expected, start.ArgumentList);
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
    [InlineData("write-uncertain")]
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
        Assert.True(changed.CanAccept && !changed.Complete);
        Assert.True(File.Exists(fake.Updater.JournalPath));
        Assert.True((await fake.Updater.CheckConnectionAsync(true, CancellationToken.None)).Complete);

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
        internal PedalCalibration Calibration { get; set; } = InitialCalibration;
        internal bool ToolFailure { get; set; }
        internal bool OtherDfu { get; set; }
        internal bool InBootloader { get; set; }
        internal string BootFailure { get; set; } = "";
        internal int Commands { get; private set; }
        internal int Programs { get; private set; }
        internal bool JournalBeforeBoot { get; private set; }
        internal Task<FirmwareToolResult>? ProgramBarrier { get; set; }
        internal FirmwareToolResult Result { get; set; } = Success;
        internal string ImagePath => Path.Combine(directory, "image.dfu");
        internal string ToolPath => Path.Combine(directory, "DfuSeCommand.exe");
        internal FirmwareUpdater Updater { get; }
        private static readonly HidDeviceInfo Device = new("fake-pedal", PedalInputReport.VendorId, PedalInputReport.ProductId, 0x200, 0xFF00, 1, 0, 0, 17);
        internal FakeFirmware()
        {
            Directory.CreateDirectory(directory); File.WriteAllBytes(ImagePath, Bytes); File.WriteAllText(ToolPath, "test tool");
            Updater = new(Path.Combine(directory, "updates"), new(
                _ => Task.FromResult(new FirmwareConnection(new(Device, Serial, this, this), new("usb-pedals", "port-1"), Calibration)),
                () => OtherDfu ? [new("other", "port-2")] : !InBootloader || BootFailure == "missing" ? []
                    : BootFailure == "multiple" ? [new("boot", "port-1"), new("other", "port-2")]
                    : [new("boot", BootFailure == "wrong-port" ? "port-2" : "port-1", BootFailure != "no-driver")],
                (_, _) => ToolFailure ? Task.FromException(new IOException("Missing ST tool")) : Task.CompletedTask,
                (_, _, _) => { Programs++; return ProgramBarrier ?? Task.FromResult(Result); },
                (_, _) => Task.CompletedTask,
                bytes => new("017", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, 0x0800C000, bytes.Length)));
        }
        internal Task<FirmwarePlan> Prepare() => Updater.PrepareAsync(ImagePath, ToolPath, CancellationToken.None);
        internal Task<FirmwareUpdateRecord> Flash(FirmwarePlan plan) => Updater.FlashAsync(plan, new InlineProgress(_ => { }), CancellationToken.None);
        internal void Reconnect() { InBootloader = false; Serial = "PE001700"; }
        public Task<byte[]> ReadAsync(HidDeviceInfo device, byte id, CancellationToken token) => Task.FromResult(PedalCalibrationReport.Encode(Calibration));
        public Task WriteAsync(HidDeviceInfo device, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            Assert.Equal(PedalCommandReport.Encode(PedalDeviceCommand.EnterBootloader), bytes.ToArray());
            Commands++; JournalBeforeBoot = File.Exists(Updater.JournalPath); InBootloader = true;
            return BootFailure == "write-uncertain" ? Task.FromException(new IOException("Write completion uncertain")) : Task.CompletedTask;
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
}

[Collection("WPF UI")]
public sealed class FirmwareUpdateUiTests
{
    [Fact]
    public Task FirmwareControlsUseThePreparedPlanAndStartupPendingUpdateBlocksLiveInput() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var fake = new FirmwareUpdateTests.FakeFirmware();
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using (var main = new MainWindow(new(), desktopServices: desktop.Services with { Firmware = fake.Updater }))
        {
            await main.RequestPanelModeAsync(SidePanelMode.Settings);
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            model.Firmware.ImagePath = fake.ImagePath; model.Firmware.ToolPath = fake.ToolPath;
            await main.PrepareFirmwareAsync();
            Assert.NotNull(model.Firmware.Plan);
            Assert.Equal(0, fake.Commands);
            var content = Assert.IsAssignableFrom<FrameworkElement>(main.Content);
            content.Measure(new(1340, 900)); content.Arrange(new(0, 0, 1340, 900)); content.UpdateLayout();
            Assert.True(Assert.IsType<Button>(main.FindName("FlashFirmwareButton")).IsEnabled);
            await main.RunFirmwareProgrammingAsync(model.Firmware.Plan, false);
            Assert.True(model.Firmware.Blocked);
            Assert.Null(model.Firmware.Plan);
        }
        var inputStarts = 0;
        await using (var restarted = new MainWindow(new(), desktopServices: desktop.Services with
        {
            Firmware = fake.Updater,
            Runtime = new(token => { inputStarts++; throw new InvalidOperationException("Input must stay blocked"); }, () => throw new InvalidOperationException("Output must stay blocked"))
        }))
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
