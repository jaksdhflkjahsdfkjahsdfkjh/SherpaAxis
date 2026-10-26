using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Setup;

namespace SherpaAxis.App;

public partial class ZipUninstallWindow : Window
{
    private readonly Func<bool, bool, IProgress<string>, Task<bool>> remove;
    private readonly bool canRemoveDrivers;
    private bool running;
    internal bool Completed { get; private set; }
    internal bool DataRemoved { get; private set; }
    public ZipUninstallWindow() : this(WindowsPrerequisiteOwnership.Read(), ZipUninstall.PrepareAsync) { }
    internal ZipUninstallWindow(IReadOnlyList<OwnedPrerequisite> owned, Func<bool, bool, IProgress<string>, Task<bool>> remove)
    {
        this.remove = remove;
        canRemoveDrivers = owned.Count > 0;
        InitializeComponent();
        Localizer.Bind(this, TitleProperty, "Uninstall SherpaAxis");
        Localizer.Bind(Heading, TextBlock.TextProperty, "Uninstall SherpaAxis");
        Localizer.Bind(Explanation, TextBlock.TextProperty, "The extracted app files will be removed after you close this window. Saved data and shared drivers are kept unless you select their removal below.");
        Localizer.Bind(DriverOption, TextBlock.TextProperty, "Remove drivers installed by SherpaAxis");
        Localizer.Bind(DataOption, TextBlock.TextProperty, "Delete all saved SherpaAxis data");
        Localizer.Bind(DataWarning, TextBlock.TextProperty, "Permanently deletes this account's profiles, settings, bindings, logs and calibration/firmware recovery backups, plus shared SherpaAxis setup records. Complete pending recovery or export needed backups first. Files exported elsewhere and Windows driver configurations are kept.");
        Localizer.Bind(RemoveButton, ContentControl.ContentProperty, "Uninstall");
        Localizer.Bind(CloseButton, ContentControl.ContentProperty, "Cancel");
        Localizer.Bind(LogsButton, ContentControl.ContentProperty, "Open setup logs");
        RemoveDrivers.IsEnabled = owned.Count > 0;
        Localizer.Bind(OwnedDrivers, TextBlock.TextProperty, owned.Count == 0 ? "No driver ownership was recorded. Existing drivers will be kept."
            : $"Eligible recorded components: {string.Join(", ", owned.Select(item => item.Component))}. Other applications may need these shared drivers.");
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        Closing += (_, args) => args.Cancel = running;
    }
    private async void RemoveClick(object sender, RoutedEventArgs e) => await RunAsync();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    internal async Task RunAsync()
    {
        if (running || Completed) { return; }
        running = true;
        RemoveButton.IsEnabled = CloseButton.IsEnabled = RemoveDrivers.IsEnabled = RemoveSavedData.IsEnabled = false;
        try
        {
            var deleteData = RemoveSavedData.IsChecked == true;
            var restart = await remove(RemoveDrivers.IsChecked == true, deleteData, new Progress<string>(SetStatus));
            DataRemoved = deleteData;
            Completed = true;
            LogsButton.IsEnabled = !DataRemoved;
            Localizer.Bind(CloseButton, ContentControl.ContentProperty, "Close");
            SetStatus(restart ? "Uninstall is ready. Close this window to remove the app files, then restart Windows."
                : DataRemoved ? "Saved SherpaAxis data was deleted. Close this window to remove the app files."
                    : "Uninstall is ready. Close this window to remove the app files. Your profiles and backups are kept.");
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        {
            DiagnosticLog.Default.Write("uninstall", "ZIP uninstall needs attention.", failure);
            SetStatus(failure.Message);
            RemoveButton.IsEnabled = true;
        }
        finally { running = false; CloseButton.IsEnabled = true; RemoveDrivers.IsEnabled = !Completed && canRemoveDrivers; RemoveSavedData.IsEnabled = !Completed; }
    }
    private void SetStatus(string message) => Localizer.Bind(StatusText, TextBlock.TextProperty, message);
    private void LogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Directory.Exists(WindowsPrerequisiteSetup.LogDirectory) ? WindowsPrerequisiteSetup.LogDirectory : DiagnosticLog.Default.DirectoryPath;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure)) { SetStatus(failure.Message); }
    }
}
