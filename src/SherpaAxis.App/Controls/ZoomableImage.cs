using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SherpaAxis.App.Controls;

/// <summary>A fitted screenshot with pointer-anchored wheel zoom and drag panning.</summary>
public sealed class ZoomableImage : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(ZoomableImage),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, (owner, _) => ((ZoomableImage)owner).SourceChanged()));

    private readonly Image picture = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer viewport;
    private readonly TimeProvider clock;
    private long? lastZoomOut;
    private double scale = 1;
    private double fittedScale = 1;
    private Point? dragStart;
    private Point dragOffset;

    public ZoomableImage() : this(TimeProvider.System) { }

    internal ZoomableImage(TimeProvider clock)
    {
        this.clock = clock;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        viewport = new ScrollViewer
        {
            Content = picture,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            CanContentScroll = false,
            Focusable = false,
            PanningMode = PanningMode.Both
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        Children.Add(viewport);
        SizeChanged += (_, _) => ResizeToFit();
        LostMouseCapture += (_, _) => dragStart = null;
    }

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    // Enlarged previews fill the window; inline examples retain their compact natural size.
    public bool FitToViewport { get; set; }
    internal double ZoomScale => scale;
    internal double FitScale => fittedScale;
    internal ScrollViewer Viewport => viewport;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Source is { Width: > 0, Height: > 0 } source && double.IsPositiveInfinity(constraint.Height))
        {
            var width = Math.Min(source.Width, constraint.Width);
            var fittedSize = new Size(width, width * source.Height / source.Width);
            // Reserve the viewport from the source aspect ratio, not the image's previous
            // zoom size. Otherwise widening the guide retains the old, shorter viewport.
            base.MeasureOverride(fittedSize);
            return fittedSize;
        }
        return base.MeasureOverride(constraint);
    }

    public void ResetZoom()
    {
        if (scale > fittedScale + 0.000001) { lastZoomOut = clock.GetTimestamp(); }
        ResizeToFit();
        SetScale(fittedScale);
        viewport.UpdateLayout();
        viewport.ScrollToHorizontalOffset(0);
        viewport.ScrollToVerticalOffset(0);
        viewport.UpdateLayout();
    }

    internal void ZoomAt(double factor, Point anchor)
    {
        var next = Math.Clamp(scale * factor, fittedScale, Math.Max(8, fittedScale));
        if (Math.Abs(next - scale) < 0.000001) { return; }
        if (next < scale) { lastZoomOut = clock.GetTimestamp(); }
        var ratio = next / scale;
        var left = (viewport.HorizontalOffset + anchor.X) * ratio - anchor.X;
        var top = (viewport.VerticalOffset + anchor.Y) * ratio - anchor.Y;
        SetScale(next);
        // Update extents before applying the new offsets so the same pixel stays under the cursor.
        viewport.UpdateLayout();
        viewport.ScrollToHorizontalOffset(left);
        viewport.ScrollToVerticalOffset(top);
        viewport.UpdateLayout();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (e.Delta < 0 && scale <= fittedScale + 0.000001)
        {
            // Consume the tail of the zoom gesture before handing later wheel input
            // to the guide. Blocked wheel events do not restart the 750 ms delay.
            if (lastZoomOut is { } timestamp && clock.GetElapsedTime(timestamp) < TimeSpan.FromMilliseconds(750)) { return; }
            if ((VisualTreeHelper.GetParent(this) ?? Parent) is UIElement parent)
            {
                parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                { RoutedEvent = MouseWheelEvent, Source = parent });
            }
            return;
        }
        ZoomAt(Math.Pow(1.2, e.Delta / 120d), e.GetPosition(viewport));
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetZoom(); e.Handled = true;
        }
        else if (scale > fittedScale + 0.000001)
        {
            dragStart = e.GetPosition(this);
            dragOffset = new Point(viewport.HorizontalOffset, viewport.VerticalOffset);
            CaptureMouse(); e.Handled = true;
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (dragStart is { } start && IsMouseCaptured)
        {
            var movement = e.GetPosition(this) - start;
            viewport.ScrollToHorizontalOffset(dragOffset.X - movement.X);
            viewport.ScrollToVerticalOffset(dragOffset.Y - movement.Y);
            e.Handled = true;
        }
        base.OnPreviewMouseMove(e);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) { ReleaseMouseCapture(); e.Handled = true; }
        base.OnPreviewMouseLeftButtonUp(e);
    }

    private void SourceChanged()
    {
        picture.Source = Source;
        lastZoomOut = null;
        fittedScale = 1;
        SetScale(1);
        ResizeToFit();
        viewport.ScrollToHorizontalOffset(0);
        viewport.ScrollToVerticalOffset(0);
    }

    private void ResizeToFit()
    {
        if (Source is not { Width: > 0, Height: > 0 } source || ActualWidth <= 0 || ActualHeight <= 0) { return; }
        var zoomRatio = scale / fittedScale;
        var availableScale = Math.Min(ActualWidth / source.Width, ActualHeight / source.Height);
        fittedScale = FitToViewport ? availableScale : Math.Min(1, availableScale);
        SetScale(Math.Clamp(fittedScale * zoomRatio, fittedScale, Math.Max(8, fittedScale)));
    }

    private void SetScale(double value)
    {
        scale = value;
        if (Source is not { Width: > 0, Height: > 0 } source) { return; }
        picture.Width = source.Width * scale;
        picture.Height = source.Height * scale;
        Cursor = scale > fittedScale + 0.000001 ? Cursors.Hand : Cursors.Arrow;
    }
}
