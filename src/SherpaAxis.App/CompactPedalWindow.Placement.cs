using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SherpaAxis.App.Services.Desktop;

namespace SherpaAxis.App;

internal sealed partial class CompactPedalWindow
{
    private bool placementReady, capturingPlacement;

    private void InitializePlacement()
    {
        Loaded += (_, _) => { RestorePlacement(); placementReady = true; RememberPlacement(); };
        LocationChanged += (_, _) => RememberPlacement();
        Closing += (_, _) => RememberPlacement();
    }

    private void RememberPlacement()
    {
        if (!placementReady || closed || capturingPlacement || model.Settings.Loading || WindowState != WindowState.Normal) { return; }
        capturingPlacement = true;
        try
        {
            var position = PointToScreen(new Point());
            model.Settings.SetPosition(position.X, position.Y);
        }
        finally { capturingPlacement = false; }
    }

    private void RestorePlacement(bool remember = false)
    {
        if (closed || capturingPlacement) { return; }
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) { return; }
        capturingPlacement = true;
        try
        {
            var desired = model.Settings.Left is { } x && model.Settings.Top is { } y ? new Point(x, y) : PointToScreen(new Point());
            var dpi = VisualTreeHelper.GetDpi(this);
            var workAreas = System.Windows.Forms.Screen.AllScreens.Select(screen => new Rect(screen.WorkingArea.X, screen.WorkingArea.Y,
                screen.WorkingArea.Width, screen.WorkingArea.Height)).ToArray();
            var position = OverlayPlacement.Constrain(desired, new Size(Width * dpi.DpiScaleX, Height * dpi.DpiScaleY), workAreas);
            // Keep the panel topmost without activating it or changing its size.
            if (!SetWindowPos(handle, 0, (int)Math.Round(position.X), (int)Math.Round(position.Y), 0, 0, 0x0015))
            { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        }
        finally { capturingPlacement = false; }
        if (remember) { RememberPlacement(); }
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
