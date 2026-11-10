using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class OverlayResizeTests
{
    [Theory]
    [InlineData("ResizeLeft", 616, 140, 224, 200)]
    [InlineData("ResizeRight", 664, 140, 200, 200)]
    [InlineData("ResizeTop", 640, 124, 200, 216)]
    [InlineData("ResizeBottom", 640, 156, 200, 200)]
    [InlineData("ResizeTopLeft", 616, 124, 224, 216)]
    [InlineData("ResizeTopRight", 664, 124, 200, 216)]
    [InlineData("ResizeBottomLeft", 616, 156, 224, 200)]
    [InlineData("ResizeBottomRight", 664, 156, 200, 200)]
    public Task EveryEdgeAndCornerResizesWithoutMovingTheOppositeEdge(string name, double width, double height, double left, double top)
        => WithWindow(async (main, view, desktop) =>
        {
            var grip = Assert.IsType<Thumb>(view.FindName(name));
            Assert.False(grip.Focusable); Assert.False(grip.IsTabStop);
            grip.RaiseEvent(new DragStartedEventArgs(0, 0));
            grip.RaiseEvent(new DragDeltaEventArgs(24, 16));
            grip.RaiseEvent(new DragCompletedEventArgs(24, 16, false));
            view.UpdateLayout();
            Assert.Equal(new Rect(left, top, width, height), new Rect(view.Left, view.Top, view.Width, view.Height));
            var settings = ((CompactPedalViewModel)view.DataContext).Settings;
            Assert.Equal(width, settings.Width); Assert.Equal(height, settings.Height);
            await main.SavePreferencesAsync();
            Assert.Equal(width, desktop.Store.Value.Overlay!.Width);
            Assert.Equal(height, desktop.Store.Value.Overlay.Height);
            view.Close();
            var reopened = main.OpenCompactView();
            Assert.Equal(width, reopened.Width); Assert.Equal(height, reopened.Height);
        });

    [Fact]
    public Task ResizeLimitsKeepTheOppositeCornerFixedAndAllowGrowingAgain() => WithWindow(async (_, view, _) =>
    {
        var grip = Assert.IsType<Thumb>(view.FindName("ResizeTopLeft"));
        var right = view.Left + view.Width; var bottom = view.Top + view.Height;
        grip.RaiseEvent(new DragStartedEventArgs(0, 0));
        grip.RaiseEvent(new DragDeltaEventArgs(2000, 2000));
        Assert.Equal(360, view.Width); Assert.Equal(110, view.Height);
        Assert.Equal(right, view.Left + view.Width); Assert.Equal(bottom, view.Top + view.Height);
        grip.RaiseEvent(new DragDeltaEventArgs(-24, -16));
        Assert.Equal(384, view.Width); Assert.Equal(126, view.Height);
        grip.RaiseEvent(new DragDeltaEventArgs(-2000, -2000));
        Assert.Equal(1000, view.Width); Assert.Equal(220, view.Height);
        Assert.Equal(right, view.Left + view.Width); Assert.Equal(bottom, view.Top + view.Height);
        grip.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
        Assert.True(view.IsVisible); Assert.False(view.ShowActivated);
        await Task.CompletedTask;
    });

    [Fact]
    public Task CancelledResizeRestoresTheWindowAndSavedSize() => WithWindow(async (main, view, desktop) =>
    {
        var original = new Rect(view.Left, view.Top, view.Width, view.Height);
        var grip = Assert.IsType<Thumb>(view.FindName("ResizeBottomRight"));
        grip.RaiseEvent(new DragStartedEventArgs(0, 0));
        grip.RaiseEvent(new DragDeltaEventArgs(53.5, 17.25));
        Assert.InRange(view.Width, 693, 694); Assert.InRange(view.Height, 157, 158);
        var settings = ((CompactPedalViewModel)view.DataContext).Settings;
        Assert.Equal(view.Width, settings.Width); Assert.Equal(view.Height, settings.Height);
        grip.RaiseEvent(new DragCompletedEventArgs(53.5, 17.25, true));
        Assert.Equal(original, new Rect(view.Left, view.Top, view.Width, view.Height));
        await main.SavePreferencesAsync();
        Assert.Null(desktop.Store.Value.Overlay);
    });

    private static Task WithWindow(Func<MainWindow, CompactPedalWindow, SettingsAndTrayTests.FakeDesktop, Task> action)
        => HardwarePedalUiTests.OnUiAsync(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-overlay-resize-" + Guid.NewGuid().ToString("N"));
            try
            {
                var desktop = new SettingsAndTrayTests.FakeDesktop();
                await using var main = new MainWindow(new(), desktopServices: desktop.Services, profileLibrary: new(folder));
                await main.InitializeForLaunchAsync();
                var view = main.OpenCompactView(); view.Left = view.Top = 200;
                await action(main, view, desktop);
            }
            finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
        });
}
