using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Output;

namespace SherpaAxis.App.Services.Setup;

internal sealed record ZipFileEntry(string Path, string Hash);
internal sealed record ZipPackageManifest(int SchemaVersion, ZipFileEntry[] Files);

internal static class ZipUninstall
{
    internal const string ManifestName = "uninstall-manifest.json";
    internal static bool IsLaunch(string[] arguments, string? executable) => arguments is ["--uninstall-zip"]
        || arguments.Length == 0 && string.Equals(Path.GetFileName(executable), "Uninstall.exe", StringComparison.OrdinalIgnoreCase);

    internal static ZipPackageManifest ReadManifest(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) { throw new IOException("Uninstall cannot run from a linked folder."); }
        var path = Path.Combine(root, ManifestName);
        if (new FileInfo(path).Length > 1_048_576) { throw new InvalidDataException("ZIP uninstall manifest is too large."); }
        ZipPackageManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ZipPackageManifest>(File.ReadAllText(path)) ?? throw new InvalidDataException("ZIP uninstall manifest is empty."); }
        catch (JsonException failure) { throw new InvalidDataException("ZIP uninstall manifest could not be read.", failure); }
        if (manifest.SchemaVersion != 1 || manifest.Files is not { Length: > 0 and <= 2000 }) { throw new InvalidDataException("ZIP uninstall manifest is invalid."); }
        foreach (var file in manifest.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || file.Path.Any(char.IsControl) || Path.IsPathRooted(file.Path) || file.Path.Split('/', '\\').Any(part => part is ".." or "." or "")
                || file.Path.Contains(':', StringComparison.Ordinal) || file.Hash is not { Length: 64 } || !file.Hash.All(Uri.IsHexDigit))
            { throw new InvalidDataException("ZIP uninstall manifest contains an invalid file path or hash."); }
        }
        if (!manifest.Files.Any(file => file.Path == "SherpaAxis.App.exe")) { throw new InvalidDataException("ZIP uninstall manifest does not identify SherpaAxis."); }
        return manifest;
    }

    internal static void RequireApplicationsClosed(int? parent = null)
    {
        VendorControl.RequireClosed();
        foreach (var process in Process.GetProcessesByName("SherpaAxis.App"))
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && process.Id != parent)
                { throw new IOException("Exit SherpaAxis completely, including its tray instance, and close games and other vJoy feeders before uninstalling."); }
            }
        }
    }

    internal static async Task<bool> PrepareAsync(bool removeDrivers, bool removeData, IProgress<string> progress)
    {
        _ = ReadManifest(AppContext.BaseDirectory);
        RequireApplicationsClosed();
        using (var visibility = WindowsHidHide.Default()) { await visibility.ReleaseAsync(CancellationToken.None); }
        VJoyDeviceName.Default().Restore();
        var startup = new WindowsStartupRegistration(Path.Combine(AppContext.BaseDirectory, "SherpaAxis.App.exe"));
        if (!removeDrivers && !removeData) { startup.RemoveIfOwned(); return false; }
        progress.Report("Removing selected SherpaAxis components and saved data…");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SherpaAxis.App.exe"))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        var dataArgument = "--remove-saved-data=" + SavedDataRemoval.CurrentUserSid;
        start.ArgumentList.Add(removeDrivers ? "--remove-owned-prerequisites" : dataArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (removeDrivers && removeData) { start.ArgumentList.Add(dataArgument); }
        using var helper = Process.Start(start) ?? throw new IOException("Could not start driver removal.");
        await helper.WaitForExitAsync();
        if (helper.ExitCode is not (0 or 3010)) { throw new IOException("Uninstall cleanup needs attention. Review the setup logs before retrying; the app folder was kept."); }
        startup.RemoveIfOwned();
        return helper.ExitCode == 3010;
    }

    internal static void ScheduleCleanup(bool removeData = false)
    {
        _ = ReadManifest(AppContext.BaseDirectory);
        var worker = Path.Combine(Path.GetTempPath(), "SherpaAxis-uninstall-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tools", "Remove-ZipPackage.ps1"), worker);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        using var current = Process.GetCurrentProcess();
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", worker,
            "-Root", AppContext.BaseDirectory, "-ApplicationPid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ApplicationStarted", current.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) })
        { start.ArgumentList.Add(value); }
        if (removeData) { start.ArgumentList.Add("-RemoveSavedData"); }
        using var process = Process.Start(start) ?? throw new IOException("Could not schedule ZIP file cleanup.");
    }
}
