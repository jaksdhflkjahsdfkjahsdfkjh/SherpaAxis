using System.IO;

namespace SherpaAxis.App.Services.Persistence;

internal static class AppDataPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SherpaAxis");
    public static string Profiles => Path.Combine(Root, "Profiles");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Logs => Path.Combine(Root, "Logs");
    public static string HardwareBackups => Path.Combine(Root, "HardwareBackups");
    public static string FirmwareUpdates => Path.Combine(Root, "FirmwareUpdates");
    public static string VirtualDevice => Path.Combine(Root, "virtual-device.txt");
}
