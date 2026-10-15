using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SherpaAxis.App.Controls;
using SherpaAxis.App.Models;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class FilterExampleTests
{
    [Fact]
    public Task EveryFilterHasTwoPackagedScreenshotsAndTheGuideCanOpenAnEnlargedPreview() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var algorithm in FilteringGuide.Algorithms)
        {
            Assert.Equal(2, algorithm.Examples.Count);
            foreach (var example in algorithm.Examples)
            {
                Assert.True(paths.Add(example.ImagePath));
                var resource = Application.GetResourceStream(new Uri(example.ImagePath, UriKind.Relative));
                Assert.NotNull(resource);
                using var stream = resource.Stream;
                var frame = Assert.Single(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames);
                Assert.Equal(1280, frame.PixelWidth); Assert.Equal(520, frame.PixelHeight);
                Assert.Equal(192, frame.DpiX, 1);
            }
        }
        Assert.Equal(14, paths.Count);
        var help = new FilteringHelpWindow(main.Resources) { Opacity = 0, ShowInTaskbar = false };
        Assert.Equal(Math.Min(1440, SystemParameters.WorkArea.Width), help.Width);
        try
        {
            help.Show();
            var content = Assert.IsAssignableFrom<FrameworkElement>(help.Content);
            content.UpdateLayout();
            foreach (var examples in Descendants(content).OfType<Expander>().ToArray()) { examples.IsExpanded = true; }
            content.UpdateLayout();
            var images = Descendants(content).OfType<Image>().ToArray();
            Assert.Equal(14, images.Length);
            Assert.All(images, image => Assert.IsAssignableFrom<BitmapSource>(image.Source));
            AssertFittedThumbnails(content);
            var panels = Descendants(content).OfType<ResponsiveExamplesPanel>().ToArray();
            Assert.Equal(7, panels.Length);
            if (SystemParameters.WorkArea.Width >= 1440)
            {
                Assert.All(panels, panel => Assert.Equal(0, panel.Children[1].TranslatePoint(new Point(), panel).Y));
            }
            help.Width = 720;
            content.UpdateLayout();
            Assert.All(panels, panel => Assert.True(panel.Children[1].TranslatePoint(new Point(), panel).Y > 0));
            help.Width = 1540;
            content.UpdateLayout();
            Assert.All(panels, panel =>
            {
                Assert.Equal(0, panel.Children[1].TranslatePoint(new Point(), panel).Y);
                Assert.True(panel.Children[1].TranslatePoint(new Point(), panel).X > 0);
            });
            Assert.All(images, image => Assert.InRange(image.ActualWidth, 1, 640));
            AssertFittedThumbnails(content);
            help.Width = 540;
            content.UpdateLayout();
            Assert.All(panels, panel => Assert.True(panel.Children[1].TranslatePoint(new Point(), panel).Y > 0));
            Assert.All(images, image => Assert.InRange(image.ActualWidth, 1, 640));
            AssertFittedThumbnails(content);
            foreach (var width in new[] { 960d, 1020, 1940, 720 })
            {
                help.Width = width;
                content.UpdateLayout();
                AssertFittedThumbnails(content);
            }
            var firstSection = Descendants(content).OfType<Expander>().First();
            firstSection.IsExpanded = false; content.UpdateLayout();
            firstSection.IsExpanded = true; content.UpdateLayout();
            AssertFittedThumbnails(content);
            var inline = Descendants(content).OfType<ZoomableImage>().First();
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            inline.RaiseEvent(wheel);
            Assert.True(wheel.Handled); // Hovering the image zooms it instead of scrolling the guide.
            Assert.True(inline.ZoomScale > inline.FitScale);
            inline.ResetZoom();
            content.UpdateLayout();
            AssertFittedThumbnails(content);
            var button = Descendants(content).OfType<Button>().First(item => item.DataContext is FilterScreenshot);
            Assert.Null(button.ToolTip);
            Exception? failure = null;
            var inspected = false;
            var inspection = help.Dispatcher.BeginInvoke(() =>
            {
                var owned = help.OwnedWindows.Cast<Window>().ToArray();
                try
                {
                    var preview = Assert.Single(owned);
                    Assert.Contains("Press and release", preview.Title, StringComparison.Ordinal);
                    Assert.Equal(1280, Assert.IsAssignableFrom<BitmapSource>(Assert.Single(Descendants(preview).OfType<Image>()).Source).PixelWidth);
                    var zoom = Assert.Single(Descendants(preview).OfType<ZoomableImage>());
                    var fitted = zoom.ZoomScale;
                    zoom.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                    Assert.True(zoom.ZoomScale > fitted);
                    zoom.ResetZoom();
                    preview.UpdateLayout();
                    Assert.Equal(fitted, zoom.ZoomScale, 5);
                    preview.WindowState = WindowState.Maximized;
                    preview.UpdateLayout();
                    var source = zoom.Source!;
                    var maximizedFit = Math.Min(zoom.ActualWidth / source.Width, zoom.ActualHeight / source.Height);
                    Assert.Equal(maximizedFit, zoom.ZoomScale, 5);
                    zoom.ZoomAt(2, new Point(200, 80));
                    zoom.ResetZoom();
                    Assert.Equal(maximizedFit, zoom.ZoomScale, 5);
                    Assert.Equal(source.Width * maximizedFit, Assert.Single(Descendants(zoom).OfType<Image>()).ActualWidth, 5);
                    inspected = true;
                }
                catch (Exception exception) { failure = exception; }
                finally { foreach (var window in owned) { window.Close(); } }
            }, DispatcherPriority.ContextIdle);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await inspection;
            Assert.Null(failure); Assert.True(inspected);
        }
        finally { help.Close(); }
    });

    [Fact]
    public Task WheelZoomKeepsThePixelUnderTheCursorAndResetRestoresTheFittedImage() => HardwarePedalUiTests.OnUiAsync(() =>
    {
        var source = BitmapSource.Create(1280, 520, 192, 192, PixelFormats.Bgra32, null, new byte[1280 * 520 * 4], 1280 * 4);
        var image = new ZoomableImage { Source = source };
        void Layout(double width, double height)
        {
            image.Measure(new Size(width, height));
            image.Arrange(new Rect(0, 0, width, height));
            image.UpdateLayout();
        }
        Layout(400, 162.5);
        var fitted = image.FitScale;
        Assert.Equal(0.625, fitted, 5);
        var anchor = new Point(200, 80);
        image.ZoomAt(2, anchor);
        Assert.Equal(fitted * 2, image.ZoomScale, 5);
        Assert.Equal(200, image.Viewport.HorizontalOffset, 5);
        Assert.Equal(80, image.Viewport.VerticalOffset, 5);
        image.ZoomAt(2, anchor);
        Assert.Equal(600, image.Viewport.HorizontalOffset, 5);
        Assert.Equal(240, image.Viewport.VerticalOffset, 5);
        image.ZoomAt(100, anchor);
        Assert.Equal(8, image.ZoomScale);
        image.ZoomAt(0.0001, anchor);
        Assert.Equal(fitted, image.ZoomScale);
        Assert.Equal(0, image.Viewport.HorizontalOffset);
        Assert.Equal(0, image.Viewport.VerticalOffset);

        image.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        Assert.True(image.ZoomScale > fitted);
        image.ResetZoom(); image.UpdateLayout();
        Assert.Equal(fitted, image.ZoomScale);
        Assert.Equal(0, image.Viewport.HorizontalOffset);

        // Fitted thumbnails never grow beyond their natural size in a maximized guide.
        Layout(1600, 900);
        Assert.Equal(1, image.ZoomScale);
        Assert.Equal(640, Assert.Single(Descendants(image).OfType<Image>()).ActualWidth);
        image.FitToViewport = true;
        Layout(1700, 1000);
        Assert.Equal(1700 / source.Width, image.ZoomScale, 5);
        return Task.CompletedTask;
    });

    [Fact]
    public Task EnlargedScreenshotFitsTheWholeViewportAfterResizeAndFitResetsZoomAndPan() => HardwarePedalUiTests.OnUiAsync(() =>
    {
        var source = BitmapSource.Create(1280, 520, 192, 192, PixelFormats.Bgra32, null, new byte[1280 * 520 * 4], 1280 * 4);
        var image = new ZoomableImage { Source = source, FitToViewport = true };
        foreach (var size in new[] { new Size(1088, 400), new Size(2200, 1100), new Size(900, 300) })
        {
            image.Measure(size); image.Arrange(new Rect(size)); image.UpdateLayout();
            var fitted = Math.Min(size.Width / source.Width, size.Height / source.Height);
            Assert.Equal(fitted, image.ZoomScale, 5);
            image.ZoomAt(2, new Point(200, 80));
            Assert.True(image.ZoomScale > fitted);
            Assert.True(image.Viewport.HorizontalOffset > 0 || image.Viewport.VerticalOffset > 0);
            image.ResetZoom();
            var picture = Assert.Single(Descendants(image).OfType<Image>());
            Assert.Equal(source.Width * fitted, picture.ActualWidth, 5);
            Assert.Equal(source.Height * fitted, picture.ActualHeight, 5);
            Assert.Equal(0, image.Viewport.HorizontalOffset);
            Assert.Equal(0, image.Viewport.VerticalOffset);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task MinimumZoomScrollsTheGuideAfter750MillisecondsWithoutExtendingTheDelay() => HardwarePedalUiTests.OnUiAsync(() =>
    {
        var clock = new ZoomClock();
        var source = BitmapSource.Create(1280, 520, 192, 192, PixelFormats.Bgra32, null, new byte[1280 * 520 * 4], 1280 * 4);
        var image = new ZoomableImage(clock) { Source = source };
        var content = new StackPanel();
        content.Children.Add(new Button { Content = image, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        content.Children.Add(new Border { Height = 2000 });
        var guide = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        guide.Measure(new Size(640, 400)); guide.Arrange(new Rect(0, 0, 640, 400)); guide.UpdateLayout();

        void Wheel(int delta)
        {
            image.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            guide.UpdateLayout();
        }
        void ScrollToTop() { guide.ScrollToTop(); guide.UpdateLayout(); }

        // An untouched, fitted screenshot allows ordinary dialog scrolling immediately.
        Wheel(-120);
        Assert.True(guide.VerticalOffset > 0);
        ScrollToTop();
        Wheel(120);
        Assert.True(image.ZoomScale > image.FitScale);
        Wheel(-1200); // Reaching minimum zoom consumes this entire wheel event.
        Assert.Equal(image.FitScale, image.ZoomScale);
        Assert.Equal(0, guide.VerticalOffset);
        Wheel(-120);
        Assert.Equal(0, guide.VerticalOffset);
        clock.Advance(TimeSpan.FromMilliseconds(749));
        Wheel(-120);
        Assert.Equal(0, guide.VerticalOffset);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Wheel(-120);
        Assert.True(guide.VerticalOffset > 0);

        // Zooming back in remains immediate, even during the protected interval.
        ScrollToTop();
        Wheel(120); Wheel(-1200);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Wheel(120);
        Assert.True(image.ZoomScale > image.FitScale);
        Assert.Equal(0, guide.VerticalOffset);
        image.ResetZoom(); // Double-click/Fit follows the same protection rule.
        Wheel(-120);
        Assert.Equal(0, guide.VerticalOffset);
        clock.Advance(TimeSpan.FromMilliseconds(750));
        Wheel(-120);
        Assert.True(guide.VerticalOffset > 0);
        return Task.CompletedTask;
    });

    [Fact]
    public Task DialogScrollingStaysActiveAcrossScreenshotsInBothDirectionsUntilTheWheelPauses() => HardwarePedalUiTests.OnUiAsync(() =>
    {
        var clock = new ZoomClock();
        var source = BitmapSource.Create(1280, 520, 192, 192, PixelFormats.Bgra32, null, new byte[1280 * 520 * 4], 1280 * 4);
        var first = new ZoomableImage(clock) { Source = source };
        var second = new ZoomableImage(clock) { Source = source };
        var text = new TextBlock { Text = "Guide description" };
        var content = new StackPanel();
        content.Children.Add(text);
        content.Children.Add(first);
        content.Children.Add(second);
        content.Children.Add(new Border { Height = 2000 });
        var guide = new GuideScrollViewer(clock) { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        guide.Measure(new Size(640, 400)); guide.Arrange(new Rect(0, 0, 640, 400)); guide.UpdateLayout();
        guide.ScrollToVerticalOffset(500); guide.UpdateLayout();
        void Wheel(UIElement target, int delta)
        {
            var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            target.RaiseEvent(preview);
            // Native WPF input bubbles the wheel only when its preview was not consumed.
            if (!preview.Handled) { target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.MouseWheelEvent }); }
            guide.UpdateLayout();
        }

        Wheel(text, -120);
        var afterDescription = guide.VerticalOffset;
        Assert.True(afterDescription > 500);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Wheel(first, 120);
        Assert.True(guide.VerticalOffset < afterDescription);
        Assert.Equal(first.FitScale, first.ZoomScale);
        var afterUp = guide.VerticalOffset;
        clock.Advance(TimeSpan.FromMilliseconds(700));
        Wheel(second, -120);
        Assert.True(guide.VerticalOffset > afterUp);
        Assert.Equal(second.FitScale, second.ZoomScale);
        clock.Advance(TimeSpan.FromMilliseconds(749));
        Wheel(second, 120);
        Assert.Equal(second.FitScale, second.ZoomScale);

        clock.Advance(TimeSpan.FromMilliseconds(750));
        var beforeZoom = guide.VerticalOffset;
        Wheel(second, 120);
        Assert.True(second.ZoomScale > second.FitScale);
        Assert.Equal(beforeZoom, guide.VerticalOffset);
        Wheel(second, -1200);
        Assert.Equal(second.FitScale, second.ZoomScale);
        Wheel(second, -120);
        Assert.Equal(beforeZoom, guide.VerticalOffset); // The existing zoom-out grace period remains intact.
        clock.Advance(TimeSpan.FromMilliseconds(750));
        Wheel(second, -120);
        Assert.True(guide.VerticalOffset > beforeZoom);
        var afterHandoff = guide.VerticalOffset;
        Wheel(first, 120); // Handoff from a fully zoomed-out image also starts a dialog gesture.
        Assert.True(guide.VerticalOffset < afterHandoff);
        Assert.Equal(first.FitScale, first.ZoomScale);
        return Task.CompletedTask;
    });

    private sealed class ZoomClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }

    private static void AssertFittedThumbnails(DependencyObject parent)
    {
        foreach (var control in Descendants(parent).OfType<ZoomableImage>())
        {
            var image = Assert.Single(Descendants(control).OfType<Image>());
            Assert.Equal(control.ActualWidth, image.ActualWidth, 1);
            Assert.Equal(control.ActualWidth * image.Source.Height / image.Source.Width, control.ActualHeight, 1);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var item in Descendants(child)) { yield return item; }
        }
    }
}
