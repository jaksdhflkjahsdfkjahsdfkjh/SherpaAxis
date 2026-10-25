using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Setup;

namespace SherpaAxis.App;

public partial class PrerequisiteSetupWindow : Window
{
    private readonly Func<IProgress<string>, Task<bool>> setup;
    private bool running;
    internal bool Ready { get; private set; }

    public PrerequisiteSetupWindow() : this(WindowsPrerequisiteSetup.RunElevatedAsync) { }
    internal PrerequisiteSetupWindow(Func<IProgress<string>, Task<bool>> setup)
    {
        this.setup = setup;
        InitializeComponent();
        Localizer.Bind(this, TitleProperty, "First-run device setup");
        Localizer.Bind(Heading, TextBlock.TextProperty, "Prepare your pedal drivers");
        Localizer.Bind(Explanation, TextBlock.TextProperty, "SherpaAxis will install missing vJoy and HidHide components, prepare the firmware USB driver and configure virtual pedals using the bundled files. Windows will ask for administrator approval. A restart may be required.");
        Localizer.Bind(CloseHint, TextBlock.TextProperty, "Close FEEL-VR Control, games and other vJoy feeders before continuing. Setup does not flash or calibrate your pedals.");
        Localizer.Bind(LogsButton, ContentControl.ContentProperty, "Open setup logs");
        Localizer.Bind(RetryButton, ContentControl.ContentProperty, "Retry");
        Localizer.Bind(ExitButton, ContentControl.ContentProperty, "Close");
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        Loaded += StartSetup;
        Closing += (_, args) => args.Cancel = running;
    }

    private async void StartSetup(object sender, RoutedEventArgs e) { Loaded -= StartSetup; await RunAsync(); }
    private async void RetryClick(object sender, RoutedEventArgs e) => await RunAsync();
    private void ExitClick(object sender, RoutedEventArgs e) => Close();
    internal async Task RunAsync()
    {
        if (running) { return; }
        running = true;
        RetryButton.IsEnabled = ExitButton.IsEnabled = false;
        Progress.IsIndeterminate = true;
        try
        {
            SetStatus("Checking required Windows drivers…");
            var restart = await setup(new Progress<string>(SetStatus));
            Ready = !restart;
            running = false;
            if (Ready) { Close(); }
            else { SetStatus("Restart Windows, then open SherpaAxis again to finish device setup."); }
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        {
            DiagnosticLog.Default.Write("prerequisite-setup", "First-run device setup needs attention.", failure);
            SetStatus(failure is Win32Exception { NativeErrorCode: 1223 }
                ? "Administrator approval was cancelled. Choose Retry to install the required drivers." : failure.Message);
            RetryButton.IsEnabled = !WindowsPrerequisiteSetup.HasActiveInstaller(WindowsPrerequisiteSetup.ReadProgress());
        }
        finally { running = false; Progress.IsIndeterminate = false; ExitButton.IsEnabled = true; }
    }

    private void SetStatus(string message) => Localizer.Bind(StatusText, TextBlock.TextProperty, message);
    private void OpenLogsClick(object sender, RoutedEventArgs e)
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
