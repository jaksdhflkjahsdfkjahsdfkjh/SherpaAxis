using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Services.Setup;

namespace SherpaAxis.App.Tests;

public sealed class ZipUninstallTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-zip-uninstall-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(directory, "App");
    private string? cleanupLog;
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private ZipFileEntry AddFile(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return new(relative, Hash(path));
    }
    private void Manifest(params ZipFileEntry[] files) => File.WriteAllText(Path.Combine(Root, ZipUninstall.ManifestName), JsonSerializer.Serialize(new ZipPackageManifest(1, files)));

    [Theory]
    [InlineData("C:\\SherpaAxis\\Uninstall.exe", true)]
    [InlineData("C:\\SherpaAxis\\UNINSTALL.EXE", true)]
    [InlineData("C:\\SherpaAxis\\SherpaAxis.App.exe", false)]
    [InlineData(null, false)]
    public void RenamedAppHostStartsUninstallOnlyWithoutOtherArguments(string? executable, bool expected)
    {
        Assert.Equal(expected, ZipUninstall.IsLaunch([], executable));
        Assert.False(ZipUninstall.IsLaunch(["--startup"], executable));
        Assert.True(ZipUninstall.IsLaunch(["--uninstall-zip"], executable));
        Assert.False(ZipUninstall.IsLaunch(["--uninstall-zip", "unexpected"], executable));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("Tools/../../outside.txt")]
    [InlineData("Tools/file.dll:stream")]
    [InlineData("Tools//file.dll")]
    public void InvalidManifestPathsAreRejectedBeforeUninstall(string relative)
    {
        var app = AddFile("SherpaAxis.App.exe", "app");
        Manifest(app, new(relative, new('A', 64)));
        Assert.Throws<InvalidDataException>(() => ZipUninstall.ReadManifest(Root));
        Assert.True(File.Exists(Path.Combine(Root, app.Path)));
    }

    [Fact]
    public async Task CleanupRemovesOnlyUnmodifiedPackageFilesAndPreservesUserFiles()
    {
        var app = AddFile("SherpaAxis.App.exe", "app");
        var library = AddFile("Tools/sample.dll", "library");
        var modified = AddFile("START-HERE.txt", "original");
        Manifest(app, library, modified);
        File.WriteAllText(Path.Combine(Root, "START-HERE.txt"), "user edits");
        File.WriteAllText(Path.Combine(Root, "custom.txt"), "keep");
        Assert.True(await RunCleanupAsync() == 0, cleanupLog);
        Assert.False(File.Exists(Path.Combine(Root, app.Path)));
        Assert.False(Directory.Exists(Path.Combine(Root, "Tools")));
        Assert.Equal("user edits", File.ReadAllText(Path.Combine(Root, "START-HERE.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Root, "custom.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmodifiedPackageFolderIsRemovedCompletely(bool removeData)
    {
        Manifest(AddFile("SherpaAxis.App.exe", "app"), AddFile("Uninstall.exe", "uninstaller"), AddFile("Tools/sample.dll", "library"));
        Assert.True(await RunCleanupAsync(removeData) == 0, cleanupLog);
        Assert.False(Directory.Exists(Root));
        if (removeData) { Assert.False(Directory.Exists(Path.Combine(directory, "UserData", "SherpaAxis"))); }
    }

    [Fact]
    public async Task WorkerRejectsEscapingPathsBeforeDeletingAnyFiles()
    {
        var app = AddFile("SherpaAxis.App.exe", "app");
        var outside = Path.Combine(directory, "outside.txt");
        File.WriteAllText(outside, "keep");
        Manifest(app, new("../outside.txt", Hash(outside)));
        Assert.Equal(1, await RunCleanupAsync());
        Assert.True(File.Exists(Path.Combine(Root, app.Path)));
        Assert.Equal("keep", File.ReadAllText(outside));
    }

    [Fact]
    public async Task WorkerPreservesFilesBehindDirectoryLinks()
    {
        var app = AddFile("SherpaAxis.App.exe", "app");
        var outside = Path.Combine(directory, "Outside");
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "outside.txt");
        File.WriteAllText(file, "keep");
        var link = Path.Combine(Root, "Linked");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var command = "$null = New-Item -ItemType Junction -Path '" + link.Replace("'", "''", StringComparison.Ordinal) + "' -Value '" + outside.Replace("'", "''", StringComparison.Ordinal) + "'";
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) { start.ArgumentList.Add(value); }
        using (var createLink = Process.Start(start)!)
        {
            var output = await WindowsPrerequisiteSetup.WaitForProcessAsync(createLink, TimeSpan.FromSeconds(15), CancellationToken.None);
            Assert.True(createLink.ExitCode == 0, output);
        }
        try
        {
            Manifest(app, new("Linked/outside.txt", Hash(file)));
            Assert.Equal(1, await RunCleanupAsync());
            Assert.Equal("keep", File.ReadAllText(file));
        }
        finally { Directory.Delete(link); }
    }

    private async Task<int> RunCleanupAsync(bool removeData = false)
    {
        var script = Path.Combine(directory, "cleanup.ps1");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tools", "Remove-ZipPackage.ps1"), script);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["LOCALAPPDATA"] = Path.Combine(directory, "UserData");
        start.Environment["TEMP"] = directory;
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-Root", Root, "-ApplicationPid", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ApplicationStarted", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) })
        { start.ArgumentList.Add(value); }
        if (removeData) { start.ArgumentList.Add("-RemoveSavedData"); }
        using var process = Process.Start(start)!;
        try
        {
            _ = await WindowsPrerequisiteSetup.WaitForProcessAsync(process, TimeSpan.FromSeconds(30), CancellationToken.None);
            var log = Path.Combine(directory, "UserData", "SherpaAxis", "Logs", "zip-uninstall.log");
            cleanupLog = File.Exists(log) ? File.ReadAllText(log) : "ZIP cleanup did not write a log.";
            return process.ExitCode;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }
}

[Collection("WPF UI")]
public sealed class ZipUninstallUiTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task DriversAreKeptByDefaultAndUnknownOwnershipCannotBeSelected(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        Localizer.Instance.Language = language;
        bool? selected = null;
        var window = new ZipUninstallWindow([], (remove, data, _) => { selected = remove; Assert.False(data); return Task.FromResult(false); }) { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            var option = Assert.IsType<CheckBox>(window.FindName("RemoveDrivers"));
            Assert.False(option.IsEnabled);
            Assert.NotEqual(true, option.IsChecked);
            Assert.NotEqual(true, Assert.IsType<CheckBox>(window.FindName("RemoveSavedData")).IsChecked);
            UiRefinementTests.CapturePreview((FrameworkElement)window.Content, $"zip-uninstall-{language}.png");
            await window.RunAsync();
            Assert.False(selected);
            Assert.True(window.Completed);
        }
        finally { window.Close(); Localizer.Instance.Language = "en"; }
    });

    [Fact]
    public Task ExplicitDriverRemovalCannotBeClosedWhileRunningAndCanRetryAfterFailure() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var owned = new OwnedPrerequisite(PrerequisiteStep.HidHide, [new("oem42.inf", new('A', 64))]);
        var window = new ZipUninstallWindow([owned], (remove, data, _) =>
        { Assert.True(remove); Assert.False(data); return ++calls == 1 ? Task.FromException<bool>(new IOException("Test failure")) : finish.Task; })
        { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            var option = Assert.IsType<CheckBox>(window.FindName("RemoveDrivers"));
            option.IsChecked = true;
            await window.RunAsync();
            Assert.False(window.Completed);
            Assert.True(option.IsEnabled);
            var operation = window.RunAsync();
            window.Close();
            Assert.True(window.IsVisible);
            finish.SetResult(true);
            await operation;
            Assert.True(window.Completed);
            Assert.Contains("restart Windows", Assert.IsType<TextBlock>(window.FindName("StatusText")).Text, StringComparison.Ordinal);
        }
        finally { finish.TrySetResult(false); window.Close(); }
    });

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task DataRemovalIsIndependentOfDriverRemovalAndCannotBeChangedWhileRunning(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        Localizer.Instance.Language = language;
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new ZipUninstallWindow([], (drivers, data, _) =>
        { Assert.False(drivers); Assert.True(data); return finish.Task; })
        { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            var option = Assert.IsType<CheckBox>(window.FindName("RemoveSavedData"));
            Assert.NotEqual(true, option.IsChecked);
            option.IsChecked = true;
            var operation = window.RunAsync();
            Assert.False(option.IsEnabled);
            window.Close();
            Assert.True(window.IsVisible);
            finish.SetResult(false);
            await operation;
            Assert.True(window.Completed && window.DataRemoved);
            Assert.False(Assert.IsType<Button>(window.FindName("LogsButton")).IsEnabled);
            Assert.Equal(Localizer.Translate("Saved SherpaAxis data was deleted. Close this window to remove the app files."),
                Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            UiRefinementTests.CapturePreview((FrameworkElement)window.Content, $"zip-uninstall-data-{language}.png");
        }
        finally { finish.TrySetResult(false); window.Close(); Localizer.Instance.Language = "en"; }
    });
}
