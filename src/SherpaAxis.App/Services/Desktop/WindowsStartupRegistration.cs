using System.IO;
using Microsoft.Win32;

namespace SherpaAxis.App.Services.Desktop;

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public sealed class WindowsStartupRegistration(string executablePath, RegistryKey? userRoot = null) : IStartupRegistration
{
    private RegistryKey UserRoot => userRoot ?? Registry.CurrentUser;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SherpaAxis";
    public bool IsEnabled
    {
        get { using var key = UserRoot.OpenSubKey(RunKey); return key?.GetValue(ValueName) is string { Length: > 0 }; }
    }
    public void SetEnabled(bool enabled)
    {
        using var key = UserRoot.CreateSubKey(RunKey, writable: true);
        if (enabled) { key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String); }
        else { key.DeleteValue(ValueName, throwOnMissingValue: false); }
    }
    internal void RemoveIfOwned()
    {
        using var key = UserRoot.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string command && command.Equals(BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase))
        { key.DeleteValue(ValueName, false); }
    }
    internal static string BuildCommand(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.Contains('"', StringComparison.Ordinal))
        { throw new ArgumentException("Startup requires the full path of the app executable.", nameof(executablePath)); }
        var command = $"\"{executablePath}\" --startup";
        return command.Length <= 260 ? command : throw new ArgumentException("Move the app to a shorter path before enabling Windows startup.", nameof(executablePath));
    }
}
