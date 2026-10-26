using System.Windows;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Firmware;
using SherpaAxis.App.Services.Output;
using SherpaAxis.App.Services.Setup;
using SherpaAxis.Devices;

namespace SherpaAxis.App;

public partial class App : Application, IDisposable
{
    private Mutex? instance;
    private bool ownsInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DiagnosticLog.Default.Write("lifecycle", "Application starting.");
        DispatcherUnhandledException += (_, args) => DiagnosticLog.Default.Write("unhandled-ui", "Unhandled UI exception.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => DiagnosticLog.Default.Write("unhandled", "Unhandled process exception.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => DiagnosticLog.Default.Write("unobserved-task", "Unobserved task exception.", args.Exception);
        if (e.Args is ["--record-prerequisite-ownership"])
        {
            try { WindowsPrerequisiteOwnership.FinalizePending(); Shutdown(0); }
            catch (Exception failure) when (OperationErrors.IsExpected(failure))
            { DiagnosticLog.Default.Write("driver-ownership", "Could not record installed drivers.", failure); Shutdown(1); }
            return;
        }
        if (e.Args.Length > 0 && (e.Args[0] is "--remove-owned-prerequisites" or "--remove-saved-data"
            || e.Args[0].StartsWith("--remove-saved-data=", StringComparison.Ordinal)))
        {
            try
            {
                int? parent = null;
                var removeDrivers = e.Args[0] == "--remove-owned-prerequisites";
                var dataArgument = removeDrivers ? e.Args[^1] : e.Args[0];
                var removeData = dataArgument == "--remove-saved-data" || dataArgument.StartsWith("--remove-saved-data=", StringComparison.Ordinal);
                var arguments = removeDrivers && removeData ? e.Args[..^1] : e.Args;
                if (arguments.Length > 2 || arguments.Length == 2 && (!int.TryParse(arguments[1], out var value) || value <= 0)) { Shutdown(1); return; }
                if (arguments.Length == 2) { parent = int.Parse(arguments[1], System.Globalization.CultureInfo.InvariantCulture); }
                WindowsPrerequisiteOwnership.RequireAdministrator();
                if (removeData && dataArgument.StartsWith("--remove-saved-data=", StringComparison.Ordinal))
                { SavedDataRemoval.RequireCurrentUser(dataArgument["--remove-saved-data=".Length..]); }
                ZipUninstall.RequireApplicationsClosed(parent);
                var restart = removeDrivers && await WindowsPrerequisiteOwnership.RemoveAsync(new Progress<string>(message =>
                    DiagnosticLog.Default.Write("uninstall", message)), CancellationToken.None);
                if (removeData)
                {
                    using var gate = new Semaphore(1, 1, @"Global\SherpaAxis.PrerequisiteSetup");
                    if (!gate.WaitOne(0)) { throw new System.IO.IOException("Driver setup is still running. Wait for it to finish before uninstalling."); }
                    try
                    {
                        if (WindowsPrerequisiteSetup.HasActiveInstaller(WindowsPrerequisiteSetup.ReadProgress()))
                        { throw new System.IO.IOException("Driver setup is still running. Wait for it to finish before uninstalling."); }
                        SavedDataRemoval.Default().Remove();
                    }
                    finally { gate.Release(); }
                }
                Shutdown(restart ? 3010 : 0);
            }
            catch (Exception failure) when (OperationErrors.IsExpected(failure))
            {
                DiagnosticLog.Default.Write("uninstall", "Uninstall cleanup did not finish.", failure);
                Shutdown(1);
            }
            return;
        }
        if (ZipUninstall.IsLaunch(e.Args, Environment.ProcessPath))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var preferences = await new Services.Persistence.ApplicationPreferencesStore(Services.Persistence.ApplicationPreferencesStore.DefaultPath).LoadAsync(CancellationToken.None);
                Localization.Localizer.Instance.Language = preferences.Language;
                var language = new SetupLanguageWindow(uninstall: true);
                language.ShowDialog();
                if (!language.Accepted) { Shutdown(0); return; }
                MainWindow = null;
                var uninstall = new ZipUninstallWindow();
                uninstall.ShowDialog();
                if (uninstall.Completed) { ZipUninstall.ScheduleCleanup(uninstall.DataRemoved); }
                Shutdown(0);
            }
            catch (Exception failure) when (OperationErrors.IsExpected(failure))
            {
                DiagnosticLog.Default.Write("uninstall", "ZIP uninstall could not start.", failure);
                MessageBox.Show(Localization.Localizer.Translate(failure.Message), "SherpaAxis", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--prepare-zip")
        {
            try
            {
                if (e.Args.Length is < 2 or > 3 || !Guid.TryParse(e.Args[1], out var run)) { Shutdown(1); return; }
                uint? selected = null;
                if (e.Args.Length == 3)
                {
                    if (!uint.TryParse(e.Args[2], out var id) || id is < 1 or > 16) { Shutdown(1); return; }
                    selected = id;
                }
                Shutdown(await WindowsPrerequisiteSetup.InstallAsync(run, selected));
            }
            catch (Exception failure) when (OperationErrors.IsExpected(failure))
            { DiagnosticLog.Default.Write("prerequisite-setup", "Could not prepare ZIP prerequisites.", failure); Shutdown(1); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--configure-dfuse")
        {
            try
            {
                if (e.Args.Length != 3) { Shutdown(1); return; }
                var restart = await WindowsFirmwareServices.ConfigureBootloaderDriverAsync(new UsbDeviceLocation(e.Args[1], e.Args[2], false));
                Shutdown(restart ? 3010 : 0);
            }
            catch (Exception failure) when (OperationErrors.IsExpected(failure))
            { DiagnosticLog.Default.Write("dfuse-driver", "Could not prepare the pedal bootloader driver.", failure); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--release-input-configuration")
        {
            var result = 0;
            try { using var visibility = WindowsHidHide.Default(); await visibility.ReleaseAsync(CancellationToken.None); }
            catch (Exception failure) when (OperationErrors.IsExpected(failure))
            { DiagnosticLog.Default.Write("input-visibility", "Could not release physical pedal hiding.", failure); result = 1; }
            try { VJoyDeviceName.Default().Restore(); }
            catch (Exception failure) when (OperationErrors.IsExpected(failure))
            { DiagnosticLog.Default.Write("vjoy-name", "Could not restore the previous controller display name.", failure); result = 1; }
            Shutdown(result);
            return;
        }
        if (e.Args.Contains("--configure-vjoy", StringComparer.OrdinalIgnoreCase))
        {
            // Installer/elevated helper must run before the normal single-instance activation path.
            try
            {
                uint? selected = e.Args.Length == 2 && uint.TryParse(e.Args[1], out var id) && id is >= 1 and <= 16 ? id : null;
                if (e.Args.Length > 2 || e.Args.Length == 2 && selected is null) { Shutdown(1); return; }
                var result = await VJoySetup.Default().ConfigureAsync(selected, CancellationToken.None);
                Shutdown(result.Ready ? 0 : result.RestartRequired ? 3010 : 1);
            }
            catch (Exception failure) when (failure is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
            { DiagnosticLog.Default.Write("vjoy-setup", "Automatic vJoy setup failed.", failure); Shutdown(1); }
            return;
        }
        instance = new Mutex(initiallyOwned: true, @"Local\SherpaAxis.PedalsUtility", out ownsInstance);
        if (!ownsInstance)
        {
            if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase)) { InstanceActivation.ShowRunningInstance(); }
            Shutdown();
            return;
        }
        if (WindowsPrerequisiteSetup.IsZipPackage && !WindowsPrerequisiteSetup.IsReady())
        {
            try
            {
                var preferences = await new Services.Persistence.ApplicationPreferencesStore(Services.Persistence.ApplicationPreferencesStore.DefaultPath).LoadAsync(CancellationToken.None);
                Localization.Localizer.Instance.Language = preferences.Language;
            }
            catch (Exception failure) when (OperationErrors.IsExpected(failure))
            { DiagnosticLog.Default.Write("prerequisite-setup", "Could not read the setup language.", failure); }
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var language = new SetupLanguageWindow();
            language.ShowDialog();
            if (!language.Accepted) { Shutdown(); return; }
            MainWindow = null;
            var setup = new PrerequisiteSetupWindow();
            setup.ShowDialog();
            MainWindow = null;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (!setup.Ready) { Shutdown(); return; }
        }
        var window = new MainWindow();
        MainWindow = window;
        await window.InitializeForLaunchAsync();
        if (!window.IsDisposed && !window.ShouldStartHidden) { window.Show(); }
        if (!window.IsDisposed && window.ShouldOfferTour) { await window.ShowGettingStartedAsync(); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }
    public void Dispose()
    {
        if (ownsInstance) { instance?.ReleaseMutex(); ownsInstance = false; }
        instance?.Dispose();
        instance = null;
        GC.SuppressFinalize(this);
    }
}
