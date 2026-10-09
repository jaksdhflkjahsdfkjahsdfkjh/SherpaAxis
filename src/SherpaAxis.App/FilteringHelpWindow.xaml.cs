using System.Windows;
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
}
