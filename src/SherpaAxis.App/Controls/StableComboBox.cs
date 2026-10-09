using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SherpaAxis.App.Controls;

public sealed class StableComboBox : ComboBox
{
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        // Scrolling must come from a wheel, scrollbar, or key, never pointer proximity.
        if (IsDropDownOpen && Mouse.Captured is not Thumb) { e.Handled = true; }
        base.OnPreviewMouseMove(e);
    }
}
