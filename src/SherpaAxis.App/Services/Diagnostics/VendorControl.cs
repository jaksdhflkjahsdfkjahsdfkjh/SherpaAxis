using System.Diagnostics;
using System.IO;

namespace SherpaAxis.App.Services.Diagnostics;

public static class VendorControl
{
    public const string ConflictMessage = "FEEL-VR Control is running. Exit it completely, including its tray instance, to avoid conflicting pedal settings.";

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
