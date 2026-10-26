using System.Diagnostics;
using System.IO;
using SherpaAxis.App.Services.Setup;

namespace SherpaAxis.App.Tests;

public sealed class SavedDataRemovalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-data-cleanup-" + Guid.NewGuid().ToString("N"));
    private string Local => Path.Combine(directory, "Local");
    private string Common => Path.Combine(directory, "Common");
    private int registrationsRemoved;
    private SavedDataRemoval Cleanup(string? application = null, Action? removeRegistration = null) =>
        new(Local, Common, application ?? Path.Combine(directory, "Install"), removeRegistration ?? (() => registrationsRemoved++));
    private static string AddFile(string parent, string relative)
    {
        var path = Path.Combine(parent, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "saved content");
        return path;
    }

    [Fact]
    public void ExplicitRemovalClearsManagedProfilesRecoveryLogsAndMachineStateButKeepsExports()
    {
        var settings = AddFile(Local, "SherpaAxis/settings.json");
        AddFile(Local, "SherpaAxis/Profiles/Race.json");
        AddFile(Local, "SherpaAxis/FirmwareUpdates/update.json");
        AddFile(Local, "SherpaAxis/HardwareBackups/raw.json");
        AddFile(Local, "SherpaAxis/Logs/app.jsonl");
        AddFile(Common, "SherpaAxis/Prerequisites/status.json");
        var exported = AddFile(directory, "Exports/calibration.json");
        var unrelated = AddFile(Local, "AnotherApp/settings.json");
        File.SetAttributes(settings, FileAttributes.ReadOnly);
        Cleanup().Remove();
        Assert.False(Directory.Exists(Path.Combine(Local, "SherpaAxis")));
        Assert.False(Directory.Exists(Path.Combine(Common, "SherpaAxis")));
        Assert.True(File.Exists(exported));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(1, registrationsRemoved);
    }

    [Fact]
    public void ElevationCannotRedirectSavedDataRemovalToAnotherWindowsAccount()
    {
        SavedDataRemoval.RequireCurrentUser(SavedDataRemoval.CurrentUserSid);
        Assert.Throws<UnauthorizedAccessException>(() => SavedDataRemoval.RequireCurrentUser("S-1-5-21-0-0-0-99999"));
    }

    [Fact]
    public void MissingDataFoldersDoNotPreventRemovingAppRegistration()
    {
        Cleanup().Remove();
        Assert.Equal(1, registrationsRemoved);
        Assert.False(Directory.Exists(Local));
    }

    [Fact]
    public void AFailedRegistrationCleanupKeepsAllSavedFilesForRetry()
    {
        var file = AddFile(Local, "SherpaAxis/settings.json");
        Assert.Throws<IOException>(() => Cleanup(removeRegistration: () => throw new IOException("Test failure")).Remove());
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void AnAppInsideItsSavedDataFolderCannotBePurgedWhileRunning()
    {
        var file = AddFile(Local, "SherpaAxis/settings.json");
        Assert.Throws<IOException>(() => Cleanup(Path.Combine(Local, "SherpaAxis", "app")).Remove());
        Assert.True(File.Exists(file));
        Assert.Equal(0, registrationsRemoved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinksWithinTheDataTreeOrItsParentAreRejectedBeforeAnyDataIsDeleted(bool linkedParent)
    {
        var outside = Path.Combine(directory, "Outside");
        var preserved = AddFile(outside, "SherpaAxis/profile.json");
        var commonFile = AddFile(Common, "SherpaAxis/setup.log");
        var link = linkedParent ? Local : Path.Combine(Local, "SherpaAxis", "Linked");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var command = "$null = New-Item -ItemType Junction -Path '" + link.Replace("'", "''", StringComparison.Ordinal)
            + "' -Value '" + outside.Replace("'", "''", StringComparison.Ordinal) + "'";
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) { start.ArgumentList.Add(value); }
        using var process = Process.Start(start)!;
        var output = await WindowsPrerequisiteSetup.WaitForProcessAsync(process, TimeSpan.FromSeconds(15), CancellationToken.None);
        Assert.True(process.ExitCode == 0, output);
        try
        {
            Assert.Throws<IOException>(() => Cleanup().Remove());
            Assert.True(File.Exists(preserved));
            Assert.True(File.Exists(commonFile));
            Assert.Equal(0, registrationsRemoved);
        }
        finally { Directory.Delete(link); }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }
}
