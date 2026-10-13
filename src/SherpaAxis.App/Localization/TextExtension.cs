using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media.Imaging;

namespace SherpaAxis.App.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding($"[{key}]") { Source = Localizer.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TranslateExtension : MarkupExtension
{
    public Binding Value { get; set; } = new();
    public bool ImagePath { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = new TranslationConverter(ImagePath) };
        binding.Bindings.Add(Value);
        binding.Bindings.Add(new Binding(nameof(Localizer.Language)) { Source = Localizer.Instance });
        return binding.ProvideValue(serviceProvider);
    }
    private sealed class TranslationConverter(bool imagePath) : IMultiValueConverter
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> Images = new(StringComparer.Ordinal);
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 0 || values[0] is null || values[0] == DependencyProperty.UnsetValue) { return ""; }
            var text = values[0].ToString() ?? "";
            if (!imagePath) { return Localizer.Translate(text); }
            return Images.GetOrAdd(LocalizedImagePath(text), path =>
            {
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri("pack://application:,,," + path, UriKind.Absolute);
                image.EndInit(); image.Freeze();
                return image;
            });
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    public static string LocalizedImagePath(string path) => Localizer.Instance.Language == "uk"
        ? path.Replace("/FilterExamples/", "/FilterExamples/uk/", StringComparison.Ordinal) : path;
}
