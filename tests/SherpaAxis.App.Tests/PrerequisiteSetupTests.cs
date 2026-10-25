using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Services.Setup;

namespace SherpaAxis.App.Tests;

public sealed class PrerequisiteSetupTests
{
    [Fact]
    public void HidHideCommandUsesTheVendorBootstrapperInterface()
    {
        var root = @"C:\Pedal tools\SherpaAxis";
        var start = WindowsPrerequisiteSetup.CreateStartInfo(PrerequisiteStep.HidHide, root, @"C:\Setup logs", null);
        Assert.Equal(Path.Combine(root, "Prerequisites", "HidHide_1.5.230_x64.exe"), start.FileName);
        Assert.Equal(["/exenoui", "/qn", "/norestart"], start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(root, start.WorkingDirectory);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task OnlyMissingDriversAreInstalledBeforeConfiguringVirtualPedals(bool vjoy, bool hidHide, bool dfu)
    {
        var calls = new List<PrerequisiteStep>();
        var verified = false;
        var inspections = 0;
        var setup = new PrerequisiteSetup(() => inspections++ == 0 ? new(vjoy, hidHide, dfu) : new(true, true, true),
            () => verified = true, (step, _) =>
            {
                Assert.True(verified);
                calls.Add(step);
                return Task.FromResult(0);
            });
        Assert.False(await setup.InstallAsync(new Progress<string>(), CancellationToken.None));
        var expected = new List<PrerequisiteStep>();
        if (!vjoy) { expected.Add(PrerequisiteStep.VJoy); }
        if (!hidHide) { expected.Add(PrerequisiteStep.HidHide); }
        if (!dfu) { expected.Add(PrerequisiteStep.DfuSe); }
        expected.Add(PrerequisiteStep.VirtualDevice);
        Assert.Equal(expected, calls);
    }

    [Theory]
    [InlineData(PrerequisiteStep.VJoy)]
    [InlineData(PrerequisiteStep.HidHide)]
    [InlineData(PrerequisiteStep.DfuSe)]
    [InlineData(PrerequisiteStep.VirtualDevice)]
    internal async Task RestartRequestsArePreservedAndDeviceConfigurationWaitsForTheRestart(PrerequisiteStep restartStep)
    {
        var calls = new List<PrerequisiteStep>();
        var inspections = 0;
        var setup = new PrerequisiteSetup(() => inspections++ == 0 ? new(false, false, false) : new(true, true, true),
            () => { }, (step, _) => { calls.Add(step); return Task.FromResult(step == restartStep ? 3010 : 0); });
        Assert.True(await setup.InstallAsync(new Progress<string>(), CancellationToken.None));
        Assert.Equal(restartStep == PrerequisiteStep.VirtualDevice, calls.Contains(PrerequisiteStep.VirtualDevice));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInstallerFailureOrTimeoutStopsFurtherSteps(bool timeout)
    {
        var calls = new List<PrerequisiteStep>();
        var setup = new PrerequisiteSetup(() => new(false, false, false), () => { }, (step, _) =>
        {
            calls.Add(step);
            return timeout ? Task.FromException<int>(new TimeoutException("Simulated stalled driver installer")) : Task.FromResult(1603);
        });
        if (timeout) { await Assert.ThrowsAsync<TimeoutException>(() => setup.InstallAsync(new Progress<string>(), CancellationToken.None)); }
        else { await Assert.ThrowsAsync<IOException>(() => setup.InstallAsync(new Progress<string>(), CancellationToken.None)); }
        Assert.Equal([PrerequisiteStep.VJoy], calls);
    }

    [Fact]
    public async Task InvalidPayloadAndCancelledSetupNeverRunAnInstaller()
    {
        var executions = 0;
        var setup = new PrerequisiteSetup(() => new(false, false, false), () => throw new InvalidDataException("Bad hash"),
            (_, _) => { executions++; return Task.FromResult(0); });
        await Assert.ThrowsAsync<InvalidDataException>(() => setup.InstallAsync(new Progress<string>(), CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.InstallAsync(new Progress<string>(), cancelled.Token));
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task MissingComponentsAfterAnInstallerReturnsZeroCannotBeReportedAsReady()
    {
        var calls = new List<PrerequisiteStep>();
        var setup = new PrerequisiteSetup(() => new(false, true, true), () => { },
            (step, _) => { calls.Add(step); return Task.FromResult(0); });
        await Assert.ThrowsAsync<IOException>(() => setup.InstallAsync(new Progress<string>(), CancellationToken.None));
        Assert.Equal([PrerequisiteStep.VJoy], calls);
    }

    [Fact]
    public void ActiveInstallerDetectionChecksProcessIdentityInsteadOfOnlyItsId()
    {
        using var process = Process.GetCurrentProcess();
        var status = new PrerequisiteProgress("test", Guid.NewGuid(), "Working", ActiveProcess: process.Id, ProcessStarted: process.StartTime.ToUniversalTime());
        Assert.True(WindowsPrerequisiteSetup.HasActiveInstaller(status));
        Assert.False(WindowsPrerequisiteSetup.HasActiveInstaller(status with { ProcessStarted = DateTimeOffset.MinValue }));
        Assert.False(WindowsPrerequisiteSetup.HasActiveInstaller(status with { ActiveProcess = int.MaxValue }));
        Assert.False(WindowsPrerequisiteSetup.HasActiveInstaller(null));
    }

    [Fact]
    public async Task ProgressCanBeReadWhileTheHelperAtomicallyReplacesItsStatus()
    {
        var root = Path.Combine(Path.GetTempPath(), "SherpaAxis-setup-progress-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "status.json");
        var status = new PrerequisiteProgress("test", Guid.NewGuid(), "Working");
        try
        {
            await WindowsPrerequisiteSetup.StoreAsync(status, path);
            var writing = Task.Run(async () =>
            {
                for (var count = 0; count < 30; count++)
                { await WindowsPrerequisiteSetup.StoreAsync(status with { Ready = count == 29 }, path); }
            });
            while (!writing.IsCompleted)
            {
                var read = WindowsPrerequisiteSetup.ReadProgress(path);
                if (read is not null) { Assert.Equal(status.Run, read.Run); }
                await Task.Yield();
            }
            await writing;
            Assert.True(WindowsPrerequisiteSetup.ReadProgress(path)!.Ready);
        }
        finally { File.Delete(path); if (Directory.Exists(root)) { Directory.Delete(root); } }
    }

    [Fact]
    public void AlteredBundledInstallerIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "SherpaAxis-zip-verification-" + Guid.NewGuid().ToString("N"));
        var prerequisites = Path.Combine(root, "Prerequisites");
        Directory.CreateDirectory(prerequisites);
        var executable = Path.Combine(prerequisites, "vJoySetup_v2.2.2.0_Win10_Win11.exe");
        try
        {
            File.WriteAllBytes(executable, [1, 2, 3]);
            Assert.Throws<InvalidDataException>(() => WindowsPrerequisiteSetup.VerifyPayload(root));
        }
        finally { File.Delete(executable); Directory.Delete(prerequisites); Directory.Delete(root); }
    }

    [Fact]
    public async Task ProcessDeadlineReturnsWithoutKillingTheRunningInstaller()
    {
        using var child = StartStub("Start-Sleep -Seconds 30");
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => WindowsPrerequisiteSetup.WaitForProcessAsync(child, TimeSpan.FromMilliseconds(100), CancellationToken.None));
            Assert.False(child.HasExited);
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); } await child.WaitForExitAsync(); }
    }

    [Fact]
    public async Task ProcessCompletionPreservesItsExitCodeAndBothOutputStreams()
    {
        using var child = StartStub("[Console]::Out.Write('setup output'); [Console]::Error.Write('setup error'); exit 7");
        var text = await WindowsPrerequisiteSetup.WaitForProcessAsync(child, TimeSpan.FromSeconds(15), CancellationToken.None);
        Assert.Equal(7, child.ExitCode);
        Assert.Contains("setup output", text, StringComparison.Ordinal);
        Assert.Contains("setup error", text, StringComparison.Ordinal);
    }

    private static Process StartStub(string command)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) { start.ArgumentList.Add(argument); }
        return Process.Start(start)!;
    }
}

[Collection("WPF UI")]
public sealed class PrerequisiteSetupUiTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task ProgressKeepsTheWindowResponsiveAndPreventsClosingDuringDriverInstallation(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new PrerequisiteSetupWindow(progress =>
        { progress.Report("Installing the vJoy driver and runtime…"); return finished.Task; })
        { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            Localizer.Instance.Language = language;
            var operation = window.RunAsync();
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            window.Close();
            Assert.True(window.IsVisible);
            Assert.False(Assert.IsType<Button>(window.FindName("ExitButton")).IsEnabled);
            Assert.Equal(Localizer.Translate("Installing the vJoy driver and runtime…"), Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            UiRefinementTests.CapturePreview((System.Windows.FrameworkElement)window.Content, $"zip-setup-{language}.png");
            finished.SetResult(true);
            await operation;
            Assert.True(Assert.IsType<Button>(window.FindName("ExitButton")).IsEnabled);
        }
        finally { finished.TrySetResult(true); window.Close(); Localizer.Instance.Language = "en"; }
    });

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task DecliningAdministratorApprovalKeepsRetryAvailableAndSuccessClosesSetup(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var attempts = 0;
        var window = new PrerequisiteSetupWindow(_ => ++attempts == 1
            ? Task.FromException<bool>(new Win32Exception(1223)) : Task.FromResult(false));
        try
        {
            Localizer.Instance.Language = language;
            await window.RunAsync();
            Assert.False(window.Ready);
            Assert.True(Assert.IsType<Button>(window.FindName("RetryButton")).IsEnabled);
            var status = Assert.IsType<TextBlock>(window.FindName("StatusText"));
            Assert.Equal(Localizer.Translate("Administrator approval was cancelled. Choose Retry to install the required drivers."), status.Text);
            await window.RunAsync();
            Assert.True(window.Ready);
            Assert.Equal(2, attempts);
        }
        finally { window.Close(); Localizer.Instance.Language = "en"; }
    });

    [Fact]
    public Task RestartRequiredDoesNotOpenTheAppOrAllowAnotherInstallation() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var window = new PrerequisiteSetupWindow(_ => Task.FromResult(true));
        try
        {
            await window.RunAsync();
            Assert.False(window.Ready);
            Assert.False(Assert.IsType<Button>(window.FindName("RetryButton")).IsEnabled);
            Assert.True(Assert.IsType<Button>(window.FindName("ExitButton")).IsEnabled);
            Assert.Contains("Restart Windows", Assert.IsType<TextBlock>(window.FindName("StatusText")).Text, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    });
}
