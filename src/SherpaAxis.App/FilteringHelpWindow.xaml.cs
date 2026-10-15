using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SherpaAxis.App.Controls;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;

namespace SherpaAxis.App;

public partial class FilteringHelpWindow : Window
{
    public FilteringHelpWindow(ResourceDictionary theme)
    {
        InitializeComponent();
        Resources.MergedDictionaries.Add(theme);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
    }

    private void OpenExampleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FilterScreenshot example }) { return; }
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri("pack://application:,,," + Localization.TranslateExtension.LocalizedImagePath(example.ImagePath), UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        var preview = new Window
        {
            Owner = this,
            Title = Localization.Localizer.Translate(example.FullTitle),
            Width = Math.Min(1120, SystemParameters.WorkArea.Width),
            Height = Math.Min(580, SystemParameters.WorkArea.Height),
            MinWidth = 400,
            MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Foreground = Foreground,
            FontFamily = FontFamily
        };
        preview.Resources.MergedDictionaries.Add(Resources);
        preview.SourceInitialized += (_, _) => WindowTheme.Apply(preview);
        var content = new DockPanel { Margin = new Thickness(16) };
        var close = new Button { Content = Localization.Localizer.Translate("Close"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(close, Dock.Bottom); content.Children.Add(close);
        var picture = new ZoomableImage { Source = image, FitToViewport = true };
        var instructions = new TextBlock { Text = Localization.Localizer.Translate("Mouse wheel: zoom at cursor · Drag: pan · Double-click: fit"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(instructions, Dock.Top); content.Children.Add(instructions);
        content.Children.Add(picture);
        preview.Content = content;
        preview.ShowDialog();
    }
}
