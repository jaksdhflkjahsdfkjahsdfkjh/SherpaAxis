using System.Windows;
using System.Windows.Controls.Primitives;

namespace SherpaAxis.App;

internal sealed partial class CompactPedalWindow
{
    private bool updatingSize;
    private Rect? resizeStart;

    private void BeginResize(object sender, DragStartedEventArgs args)
    {
        resizeStart = new Rect(Left, Top, Width, Height);
        args.Handled = true;
    }

    private void ResizeOverlay(object sender, DragDeltaEventArgs args)
    {
        if (closed || sender is not Thumb grip || resizeStart is null) { return; }
        var bounds = new Rect(Left, Top, Width, Height);
        var width = bounds.Width; var height = bounds.Height;
        var left = bounds.Left; var top = bounds.Top;
        if (grip.HorizontalAlignment == HorizontalAlignment.Left)
        {
            width = Math.Clamp(width - args.HorizontalChange, MinWidth, MaxWidth);
            left = bounds.Right - width;
        }
        else if (grip.HorizontalAlignment == HorizontalAlignment.Right) { width = Math.Clamp(width + args.HorizontalChange, MinWidth, MaxWidth); }
        if (grip.VerticalAlignment == VerticalAlignment.Top)
        {
            height = Math.Clamp(height - args.VerticalChange, MinHeight, MaxHeight);
            top = bounds.Bottom - height;
        }
        else if (grip.VerticalAlignment == VerticalAlignment.Bottom) { height = Math.Clamp(height + args.VerticalChange, MinHeight, MaxHeight); }
        SetOverlayBounds(new Rect(left, top, width, height), grip.HorizontalAlignment == HorizontalAlignment.Left, grip.VerticalAlignment == VerticalAlignment.Top);
        args.Handled = true;
    }

    private void EndResize(object sender, DragCompletedEventArgs args)
    {
        if (!closed && args.Canceled && resizeStart is { } original) { SetOverlayBounds(original); }
        resizeStart = null;
        args.Handled = true;
    }

    private void SetOverlayBounds(Rect bounds, bool anchorRight = false, bool anchorBottom = false)
    {
        updatingSize = true;
        try
        {
            Width = bounds.Width; Height = bounds.Height;
            Left = anchorRight ? bounds.Right - Width : bounds.Left;
            Top = anchorBottom ? bounds.Bottom - Height : bounds.Top;
            model.Settings.Width = Width; model.Settings.Height = Height;
        }
        finally { updatingSize = false; }
    }
}
