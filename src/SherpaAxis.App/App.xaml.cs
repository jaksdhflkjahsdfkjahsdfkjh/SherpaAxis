using System.Windows;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Firmware;
using SherpaAxis.App.Services.Output;
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
