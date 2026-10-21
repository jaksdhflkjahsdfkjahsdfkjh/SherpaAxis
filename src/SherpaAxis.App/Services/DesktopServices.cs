using System.Windows.Interop;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Firmware;
using SherpaAxis.App.Services.Hardware;
using SherpaAxis.App.Services.Input;
using SherpaAxis.App.Services.Output;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.Services.Runtime;

namespace SherpaAxis.App.Services;

public enum TrayCommand { Show, Tune, Calibrate, Settings, Exit }
public sealed record TrayMenuState(IReadOnlyList<ProfileTab> Profiles, ProfileTab? SelectedProfile,
    SidePanelMode Mode, bool CanSelectMode, bool CanSwitchProfile);
public interface IApplicationTray : IDisposable
{
    event Action<TrayCommand>? CommandRequested;
    event Action<ProfileTab>? ProfileRequested;
}
public sealed record DesktopServices(IApplicationPreferencesStore Preferences, IStartupRegistration Startup,
    Func<Func<TrayMenuState>, IApplicationTray> CreateTray, Func<HwndSource, IButtonStateSource>? Buttons = null,
    PedalRuntimeServices? Runtime = null, StartupHardwareRecovery? Recovery = null, DeviceDiagnostics? Diagnostics = null, FirmwareUpdater? Firmware = null,
    DiagnosticLog? Log = null, ProfileDraftStore? Drafts = null,
    Func<CorrectedCalibration, CancellationToken, Task>? SaveCorrectedCalibration = null)
{
    public static DesktopServices Default()
    {
        var vjoy = AutomaticVJoy.Default();
        return new(new ApplicationPreferencesStore(ApplicationPreferencesStore.DefaultPath),
        new WindowsStartupRegistration(System.IO.Path.Combine(AppContext.BaseDirectory, "SherpaAxis.App.exe")), state => new ApplicationTray(state),
            source => new WindowsButtonInput(source), PedalRuntimeServices.Default(vjoy), StartupHardwareRecovery.Default(), DeviceDiagnostics.Default(vjoy), FirmwareUpdater.Default(), DiagnosticLog.Default, ProfileDraftStore.Default(),
            new CorrectedCalibrationWriter(PedalControllerConnection.OpenAsync, HardwareRecoveryStore.DefaultDirectory).SaveAsync);
    }
}
