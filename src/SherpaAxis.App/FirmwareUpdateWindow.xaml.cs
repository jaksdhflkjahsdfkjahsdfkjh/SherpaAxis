using System.Windows;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App;

public partial class FirmwareUpdateWindow : Window
{
    private readonly FirmwareUpdateViewModel model;
    private readonly Func<Task> flash;

    public FirmwareUpdateWindow(FirmwareUpdateViewModel model, ResourceDictionary theme, Func<Task> flash)
    {
        this.model = model;
        this.flash = flash;
        InitializeComponent();
        DataContext = model;
        Resources.MergedDictionaries.Add(theme);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        Closing += (_, args) => args.Cancel = model.Running;
    }

    private async void FlashClick(object sender, RoutedEventArgs e) => await flash();
}
