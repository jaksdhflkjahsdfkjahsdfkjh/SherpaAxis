using System.Windows;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App;

public partial class FirmwareUpdateWindow : Window
{
    private readonly FirmwareUpdateViewModel model;
    private readonly Func<Task> flash;
    private readonly Func<Task>? review;
    private readonly Func<Task>? restore;
    private readonly Func<Task>? accept;

    public FirmwareUpdateWindow(FirmwareUpdateViewModel model, ResourceDictionary theme, Func<Task> flash,
        Func<Task>? review = null, Func<Task>? restore = null, Func<Task>? accept = null)
    {
        this.model = model;
        this.flash = flash;
        this.review = review;
        this.restore = restore;
        this.accept = accept;
        InitializeComponent();
        DataContext = model;
        Resources.MergedDictionaries.Add(theme);
        Localization.Localizer.Bind(ReviewFirmwareButton, System.Windows.Controls.ContentControl.ContentProperty, "Check firmware");
        Localization.Localizer.Bind(RestoreFirmwareCalibrationButton, System.Windows.Controls.ContentControl.ContentProperty, "Restore calibration backup");
        Localization.Localizer.Bind(UseDetectedControllerButton, System.Windows.Controls.ContentControl.ContentProperty, "Use detected controller");
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        Closing += (_, args) => args.Cancel = model.Running;
    }

    private async void FlashClick(object sender, RoutedEventArgs e)
    {
        if (model.Completed) { Close(); return; }
        await flash();
    }
    private async void ReviewClick(object sender, RoutedEventArgs e) { if (review is not null) { await review(); } }
    private async void RestoreCalibrationClick(object sender, RoutedEventArgs e) { if (restore is not null) { await restore(); } }
    private async void UseDetectedClick(object sender, RoutedEventArgs e) { if (accept is not null) { await accept(); } }
}
