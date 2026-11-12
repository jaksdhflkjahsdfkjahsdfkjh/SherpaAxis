using System.Globalization;
using System.Windows.Data;

namespace SherpaAxis.App.Controls;

public sealed class PedalPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double percent && double.IsFinite(percent)
            ? Math.Floor(Math.Clamp(percent, 0, 100)).ToString("0", CultureInfo.InvariantCulture) : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
