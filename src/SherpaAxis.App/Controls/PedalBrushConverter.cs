using System.Globalization;
using System.Windows.Data;
using SherpaAxis.App.Themes;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Controls;

public sealed class PedalBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is PedalAxis axis ? AppColors.ForPedal(axis) : AppColors.BrakeBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
