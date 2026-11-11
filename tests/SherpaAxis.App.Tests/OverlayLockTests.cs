using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class OverlayLockTests
{
    [Fact]
    public Task LockedOverlayPassesNativeHitTestingToTheWindowUnderneathAndUnlockRestoresEditing() => WithMain(async (main, _) =>
    {
        var underneath = new Window
        {
            Left = 20,
            Top = 20,
            Width = 800,
            Height = 400,
            Background = Brushes.Black,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true
        };
        try
        {
            underneath.Show();
            var view = main.OpenCompactView(); view.Left = 50; view.Top = 50; view.UpdateLayout();
            var settings = ((CompactPedalViewModel)view.DataContext).Settings;
            var handle = new WindowInteropHelper(view).Handle;
            var pixel = view.PointToScreen(new Point(80, 50));
            var point = new NativePoint { X = (int)pixel.X, Y = (int)pixel.Y };
            Assert.Equal(handle, WindowFromPoint(point));
            var originalStyle = GetWindowLongPtr(handle, -20).ToInt64();
            Assert.NotEqual(0, originalStyle & 0x80000); // WPF layered window.
            settings.IsLocked = true;
            Assert.Equal(originalStyle | 0x20, GetWindowLongPtr(handle, -20).ToInt64());
            Assert.Equal(new WindowInteropHelper(underneath).Handle, WindowFromPoint(point));
            Assert.False(Assert.IsType<Grid>(view.Content).IsHitTestVisible);
            var before = new Rect(view.Left, view.Top, view.Width, view.Height);
            var grip = Assert.IsType<Thumb>(view.FindName("ResizeTopLeft"));
            grip.RaiseEvent(new DragStartedEventArgs(0, 0)); grip.RaiseEvent(new DragDeltaEventArgs(30, 20));
            grip.RaiseEvent(new DragCompletedEventArgs(30, 20, false));
            Assert.Equal(before, new Rect(view.Left, view.Top, view.Width, view.Height));
            settings.IsLocked = false;
            Assert.Equal(originalStyle, GetWindowLongPtr(handle, -20).ToInt64());
            Assert.Equal(handle, WindowFromPoint(point)); Assert.True(Assert.IsType<Grid>(view.Content).IsHitTestVisible);
            await Task.CompletedTask;
        }
        finally { underneath.Close(); }
    });

    [Fact]
    public Task LockCheckboxAndTrayAgreeAndTheSavedLockSurvivesReopening() => WithMain(async (main, desktop) =>
    {
        await main.RequestPanelModeAsync(SidePanelMode.PedalOverlay);
        var view = main.OpenCompactView();
        var check = Assert.IsType<CheckBox>(main.FindName("OverlayLockedCheck"));
        check.IsChecked = true;
        Assert.True(main.GetTrayState().OverlayLocked);
        Assert.False(Assert.IsType<GroupBox>(main.FindName("OverlaySizeGroup")).IsEnabled);
        Assert.True(Assert.IsType<Slider>(main.FindName("OverlayWindowSlider")).IsEnabled);
        await main.SavePreferencesAsync(); Assert.True(desktop.Store.Value.Overlay!.Locked);
        view.Close(); view = main.OpenCompactView();
        Assert.NotEqual(0, GetWindowLongPtr(new WindowInteropHelper(view).Handle, -20).ToInt64() & 0x20);
        desktop.Tray.Command(TrayCommand.ToggleOverlayLock);
        Assert.False(check.IsChecked); Assert.False(main.GetTrayState().OverlayLocked);
        Assert.True(Assert.IsType<GroupBox>(main.FindName("OverlaySizeGroup")).IsEnabled);
        using var tray = new ApplicationTray(main.GetTrayState); tray.RebuildMenu();
        var menu = Assert.IsType<System.Windows.Forms.ContextMenuStrip>(typeof(ApplicationTray).GetField("menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray));
        var item = menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(entry => entry.Text == "Lock pedal overlay");
        Assert.True(item.Enabled); Assert.False(item.Checked);
        tray.CommandRequested += desktop.Tray.Command;
        item.PerformClick(); Assert.True(item.Checked); Assert.True(check.IsChecked);
        Assert.False(view.ShowActivated);
    });

    [Fact]
    public void OlderPreferencesDefaultToUnlockedAndNewPreferencesRoundTrip()
    {
        var settings = new PedalOverlaySettingsViewModel(); settings.Load(new());
        Assert.False(settings.IsLocked); Assert.True(settings.CanEditPlacement);
        settings.IsLocked = true;
        var copy = new PedalOverlaySettingsViewModel(); copy.Load(settings.Snapshot());
        Assert.True(copy.IsLocked); Assert.False(copy.CanEditPlacement);
    }

    private static Task WithMain(Func<MainWindow, SettingsAndTrayTests.FakeDesktop, Task> action) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-overlay-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var desktop = new SettingsAndTrayTests.FakeDesktop();
            await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services, profileLibrary: new(folder));
            await main.InitializeForLaunchAsync(); await action(main, desktop);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetWindowLongPtr(nint window, int index);
}
