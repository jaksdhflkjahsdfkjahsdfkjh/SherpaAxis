using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SherpaAxis.App;

internal sealed partial class CompactPedalWindow
{
    private void ApplyMouseTransparency()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || closed) { return; }
        const int extendedStyle = -20;
        const long transparent = 0x20;
        var current = GetWindowLongPtr(handle, extendedStyle).ToInt64();
        var desired = model.Settings.IsLocked ? current | transparent : current & ~transparent;
        if (current == desired) { return; }
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtr(handle, extendedStyle, new nint(desired)) == 0 && Marshal.GetLastWin32Error() is var error && error != 0)
        { throw new System.ComponentModel.Win32Exception(error); }
        if (!SetWindowPos(handle, 0, 0, 0, 0, 0, 0x0037)) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        if (model.Settings.IsLocked)
        {
            resizeStart = null;
            if (Mouse.Captured is DependencyObject captured && GetWindow(captured) == this) { Mouse.Capture(null); }
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
