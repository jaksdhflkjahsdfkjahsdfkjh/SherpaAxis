using System.Windows.Controls;
using System.Windows.Input;

namespace SherpaAxis.App.Controls;

/// <summary>Keeps a dialog scroll gesture active when its content moves under the pointer.</summary>
public sealed class GuideScrollViewer : ScrollViewer
{
    private readonly TimeProvider clock;
    private long? lastScrollWheel;

    public GuideScrollViewer() : this(TimeProvider.System) { }

    internal GuideScrollViewer(TimeProvider clock) => this.clock = clock;

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (lastScrollWheel is { } timestamp && clock.GetElapsedTime(timestamp) < TimeSpan.FromMilliseconds(750))
        {
            // Intercept before a screenshot can turn this ongoing page gesture into zoom.
            // A short idle pause releases the gesture so deliberate image zoom still works.
            e.Handled = true;
            RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            { RoutedEvent = MouseWheelEvent, Source = this });
            return;
        }
        base.OnPreviewMouseWheel(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        lastScrollWheel = clock.GetTimestamp();
        base.OnMouseWheel(e);
    }
}
