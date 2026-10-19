using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SherpaAxis.App.Services.Desktop;

internal static class WindowTheme
{
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var enabled = 1;
        var black = 0;
        var white = 0x00FFFFFF;
        // Older Windows versions may reject caption/text colors; keep the dark-frame request.
        _ = DwmSetWindowAttribute(handle, 20, in enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 35, in black, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, in white, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);
}
