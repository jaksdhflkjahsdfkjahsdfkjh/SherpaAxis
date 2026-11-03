using System.Diagnostics;
using System.IO;

namespace SherpaAxis.App.Services.Diagnostics;

public static class VendorControl
{
    public const string ConflictMessage = "Close FEEL-VR Control, including its tray icon, before using SherpaAxis.";

    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("FEEL_VR_Control");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) { process.Dispose(); } }
    }

    public static void RequireClosed(Func<bool>? isRunning = null)
    {
        if ((isRunning ?? IsRunning)()) { throw new IOException(ConflictMessage); }
    }
}
