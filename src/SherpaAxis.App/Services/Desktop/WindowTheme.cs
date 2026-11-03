using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SherpaAxis.App.Themes;

namespace SherpaAxis.App.Services.Desktop;

internal static class WindowTheme
{
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var enabled = 1;
        var caption = AppColors.Window.R | AppColors.Window.G << 8 | AppColors.Window.B << 16;
        var text = AppColors.Text.R | AppColors.Text.G << 8 | AppColors.Text.B << 16;
        // Older Windows versions may reject caption/text colors; keep the dark-frame request.
        _ = DwmSetWindowAttribute(handle, 20, in enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 35, in caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, in text, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);
}
