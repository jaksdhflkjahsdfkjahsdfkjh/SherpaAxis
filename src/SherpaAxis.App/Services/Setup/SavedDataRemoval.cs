using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace SherpaAxis.App.Services.Setup;

internal sealed class SavedDataRemoval(string localApplicationData, string commonApplicationData,
    string applicationDirectory, Action removeRegistration)
{
    private static readonly string[] TaskRemovalArguments = ["/Delete", "/TN", "SherpaAxis.DeviceSetup.48CBF14C", "/F"];
    internal static string CurrentUserSid
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value ?? throw new IOException("Cannot identify the Windows account for saved data removal."); }
    }
    internal static SavedDataRemoval Default(string? expectedUserSid = null)
    {
        var user = SavedDataUser.Interactive();
        user.RequireExpectedUser(expectedUserSid);
        using (var hive = Registry.Users.OpenSubKey(user.Sid, writable: true))
        { if (hive is null) { throw new IOException("The desktop account's registry is unavailable. Saved data was kept."); } }
        return new(user.LocalApplicationData,
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppContext.BaseDirectory, () =>
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in TaskRemovalArguments) { start.ArgumentList.Add(value); }
            using var task = Process.Start(start) ?? throw new IOException("Could not remove SherpaAxis setup registration.");
            var output = task.StandardOutput.ReadToEndAsync();
            var error = task.StandardError.ReadToEndAsync();
            if (!task.WaitForExit(10000)) { throw new IOException("SherpaAxis setup registration removal did not finish. Retry uninstalling."); }
            _ = output.GetAwaiter().GetResult(); _ = error.GetAwaiter().GetResult();
            if (task.ExitCode is not (0 or 1) || task.ExitCode == 1 && File.Exists(Path.Combine(Environment.SystemDirectory, "Tasks", "SherpaAxis.DeviceSetup.48CBF14C")))
            { throw new IOException("Could not remove SherpaAxis setup registration."); }
            using var hive = Registry.Users.OpenSubKey(user.Sid, writable: true)
                ?? throw new IOException("The desktop account's registry is unavailable. Retry uninstalling.");
            hive.DeleteSubKeyTree(@"Software\SherpaAxis", false);
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            machine.DeleteSubKeyTree(@"Software\SherpaAxis", false);
        });
    }

    internal void Remove()
    {
        var targets = new[] { Path.Combine(localApplicationData, "SherpaAxis"), Path.Combine(commonApplicationData, "SherpaAxis") }
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = new List<string>();
        var application = Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var root in targets)
        {
            if (Path.GetFileName(root) != "SherpaAxis" || Path.GetDirectoryName(root) is null
                || application.Equals(root, StringComparison.OrdinalIgnoreCase)
                || application.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { throw new IOException("Saved data cannot be removed while the app is running from its data folder. Move the extracted app elsewhere first."); }
            Inspect(root, root, entries);
        }
        foreach (var path in entries.Where(path => (File.GetAttributes(path) & FileAttributes.Directory) == 0))
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        foreach (var path in entries.AsEnumerable().Reverse())
        {
            var root = targets.First(target => path.Equals(target, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            RequireContained(root, path);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) { Directory.Delete(path); }
            else
            {
                if ((attributes & FileAttributes.ReadOnly) != 0) { File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly); }
                File.Delete(path);
            }
        }
        removeRegistration();
    }

    private static void Inspect(string root, string path, List<string> entries)
    {
        RequireContained(root, path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        entries.Add(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) { Inspect(root, child, entries); }
        }
    }

    private static void RequireContained(string root, string path)
    {
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { throw new IOException("Saved data cleanup found a path outside the SherpaAxis data folder."); }
        for (var parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
        {
            try
            {
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                { throw new IOException("Saved data cleanup cannot follow filesystem links. The data was kept; review the linked folder before retrying."); }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
