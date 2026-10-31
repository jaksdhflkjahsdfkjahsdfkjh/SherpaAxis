using System.IO;
using Microsoft.Win32;
using SherpaAxis.App.Services.Setup;

namespace SherpaAxis.App.Services.Updates;

public enum AppUpdateKind { Setup, Zip }

public sealed record AppUpdateInstallation(string Directory, AppUpdateKind Kind, Version Version)
{
    public string Executable => Path.Combine(Directory, "SherpaAxis.App.exe");
    public static AppUpdateInstallation Current()
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        RequireSafeDirectory(directory);
        if (File.Exists(Path.Combine(directory, ZipUninstall.ManifestName)))
        { _ = ZipUninstall.ReadManifest(directory); return new(directory, AppUpdateKind.Zip, CurrentVersion); }
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var registration = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{48CBF14C-6F5A-43EE-8F67-AE44052BA58B}_is1");
        if (registration?.GetValue("InstallLocation") is string installed &&
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installed)), directory, StringComparison.OrdinalIgnoreCase))
        { return new(directory, AppUpdateKind.Setup, CurrentVersion); }
        throw new IOException("Automatic updates require an installed or extracted release of SherpaAxis.");
    }

    private static Version CurrentVersion => typeof(AppUpdateInstallation).Assembly.GetName().Version!;

    internal static void RequireSafeDirectory(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var roots = new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData }
            .Select(Environment.GetFolderPath).Where(path => !string.IsNullOrEmpty(path))
            .Append(Path.GetPathRoot(full)!).Append(Path.GetTempPath())
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        if (roots.Any(root => string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)))
        { throw new IOException("Move the ZIP edition into its own application folder before updating."); }
        for (var parent = full; parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (System.IO.Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            { throw new IOException("Automatic updates cannot replace an application through a filesystem link."); }
        }
    }
}

public sealed record AppUpdateProgress(string Stage, int? Percent = null);
public sealed record PreparedAppUpdate(AppRelease Release, AppUpdateInstallation Installation,
    string OperationDirectory, string PackagePath, string PackageHash);
