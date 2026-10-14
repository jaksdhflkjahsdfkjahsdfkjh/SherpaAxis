using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SherpaAxis.App.Controls;

public sealed class StableComboBox : ComboBox
{
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        // A closed selector must never change a setting while the user scrolls the panel,
        // including after it has keyboard focus. Open lists keep their normal scrolling.
        if (IsDropDownOpen) { base.OnPreviewMouseWheel(e); return; }
        e.Handled = true;
        if ((VisualTreeHelper.GetParent(this) ?? Parent) is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            { RoutedEvent = MouseWheelEvent, Source = parent });
        }
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        // Scrolling must come from a wheel, scrollbar, or key, never pointer proximity.
        if (IsDropDownOpen && Mouse.Captured is not Thumb) { e.Handled = true; }
        base.OnPreviewMouseMove(e);
    }
}
