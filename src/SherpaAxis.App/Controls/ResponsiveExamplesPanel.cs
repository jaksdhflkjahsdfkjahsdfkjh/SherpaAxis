using System.Windows;
using System.Windows.Controls;

namespace SherpaAxis.App.Controls;

/// <summary>One or two readable columns without stretching the recorded screenshots.</summary>
public sealed class ResponsiveExamplesPanel : Panel
{
    private const double MinimumColumnWidth = 420;
    private const double MaximumColumnWidth = 640;
    private const double Gap = 16;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnCount(availableSize.Width);
        var width = ColumnWidth(availableSize.Width, columns);
        var height = 0d;
        for (var index = 0; index < InternalChildren.Count; index += columns)
        {
            var rowHeight = 0d;
            for (var column = 0; column < columns && index + column < InternalChildren.Count; column++)
            {
                var child = InternalChildren[index + column];
                child.Measure(new Size(width, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += rowHeight;
        }
        return new Size(width * columns + Gap * (columns - 1), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnCount(finalSize.Width);
        var width = ColumnWidth(finalSize.Width, columns);
        var top = 0d;
        for (var index = 0; index < InternalChildren.Count; index += columns)
        {
            var rowHeight = 0d;
            for (var column = 0; column < columns && index + column < InternalChildren.Count; column++)
            {
                rowHeight = Math.Max(rowHeight, InternalChildren[index + column].DesiredSize.Height);
            }
            for (var column = 0; column < columns && index + column < InternalChildren.Count; column++)
            {
                InternalChildren[index + column].Arrange(new Rect(column * (width + Gap), top, width, rowHeight));
            }
            top += rowHeight;
        }
        return finalSize;
    }

    private int ColumnCount(double width) => InternalChildren.Count > 1 && width >= 2 * MinimumColumnWidth + Gap ? 2 : 1;
    private static double ColumnWidth(double width, int columns) => Math.Max(0, Math.Min(MaximumColumnWidth, (width - Gap * (columns - 1)) / columns));
}
