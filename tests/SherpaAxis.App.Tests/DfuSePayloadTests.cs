using System.Diagnostics;
using System.IO;
using SherpaAxis.App.Services.Firmware;
using SherpaAxis.Devices;

namespace SherpaAxis.App.Tests;

public sealed class DfuSePayloadTests
{
    [Fact]
    public async Task ReviewedStToolWithoutCompanyMetadataPassesValidation()
    {
        var tool = WindowsFirmwareServices.FindTool();
        Assert.True(string.IsNullOrWhiteSpace(FileVersionInfo.GetVersionInfo(tool).CompanyName));
        await WindowsFirmwareServices.ValidateToolFilesAsync(tool, CancellationToken.None);
    }

    [Theory]
    [InlineData("DfuSeCommand.exe", false)]
    [InlineData("STDFU.dll", false)]
    [InlineData("STDFUPRT.dll", false)]
    [InlineData("STDFUFiles.dll", false)]
    [InlineData("STTubeDevice30.dll", false)]
    [InlineData("mfc120.dll", false)]
    [InlineData("msvcp120.dll", false)]
    [InlineData("msvcr120.dll", false)]
    [InlineData("DfuSeCommand.exe", true)]
    [InlineData("STDFU.dll", true)]
    [InlineData("Driver/STtube.inf", false)]
    [InlineData("Driver/sttube.cat", false)]
    [InlineData("Driver/x64/STTub30.sys", false)]
    public async Task ChangedOrMissingToolFilesAreRejectedBeforeExecution(string fileName, bool missing)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-dfuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.GetDirectoryName(WindowsFirmwareServices.FindTool())!;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(directory, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var changed = Path.Combine(directory, fileName);
            if (missing) { File.Delete(changed); }
            else
            {
                using var stream = File.Open(changed, FileMode.Open, FileAccess.ReadWrite);
                var first = stream.ReadByte(); stream.Position = 0; stream.WriteByte((byte)(first ^ 1));
            }
            // A local manifest cannot override the hashes embedded in SherpaAxis.
            File.WriteAllText(Path.Combine(directory, "sha256.json"), "{}");
            var failure = await Assert.ThrowsAsync<IOException>(() => WindowsFirmwareServices.ValidateToolFilesAsync(
                Path.Combine(directory, "DfuSeCommand.exe"), CancellationToken.None, includeDriver: fileName.StartsWith("Driver/", StringComparison.Ordinal)));
            Assert.Contains("Run SherpaAxis Setup again.", failure.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DriverPreparationRejectsANormalPedalDeviceBeforeChangingDrivers()
    {
        var device = new UsbDeviceLocation(@"USB\VID_0483&PID_A2EA\PE001700", "port-1");
        Assert.Throws<IOException>(() => WindowsDfuDriver.Bind(device,
            Path.Combine(Path.GetDirectoryName(WindowsFirmwareServices.FindTool())!, "Driver", "STtube.inf")));
    }

    [Fact]
    public async Task CancelledValidationDoesNotLaunchTheTool()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsFirmwareServices.ValidateToolFilesAsync(
            WindowsFirmwareServices.FindTool(), cancellation.Token));
    }
}
