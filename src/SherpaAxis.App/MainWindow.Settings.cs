using System.Windows;
using System.Windows.Interop;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Input;
using SherpaAxis.App.Services.Runtime;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private readonly DesktopServices desktop;
    private ApplicationPreferences preferences = new();
    private IApplicationTray? tray;
    private HwndSource? activationSource;
    private IButtonStateSource buttons = new ButtonStateBuffer();
    private PedalRuntime? runtime;
    private Task? runtimeInitialization;
    private Task? desktopInitialization;
    private Task? settingsOperation;
    private Task? visibilityOperation;
    private bool savePreferencesAgain;
    private bool preferencesLoaded;
    private bool launchInitialized;
    private bool exitRequested;
    private bool hidingToTray;
    private WindowState restoreState = WindowState.Maximized;
    internal bool ShouldStartHidden => preferences.StartInTray && tray is not null && !viewModel.Recovery.IsBlocked && !viewModel.Firmware.Blocked && !viewModel.HasRecoverableDrafts && !viewModel.VendorControlRunning && !ShouldOfferTour;
    internal bool ShouldOfferTour => launchInitialized && !preferences.TourDismissed && !viewModel.Recovery.IsBlocked && !viewModel.Firmware.Blocked && !viewModel.HasRecoverableDrafts && !viewModel.VendorControlRunning;
    internal bool IsDisposed => disposed;

    private async void DesktopLoaded(object sender, RoutedEventArgs e) { Loaded -= DesktopLoaded; await InitializeDesktopAsync(); }
    internal Task InitializeDesktopAsync() => desktopInitialization ??= InitializeDesktopCoreAsync();
    internal async Task InitializeForLaunchAsync()
    {
        await InitializeDesktopAsync(); await InitializeLibraryAsync();
        if (closing) { return; }
        RefreshVendorControlStatus();
        await CheckStartupRecoveryAsync();
        await CheckFirmwareStartupAsync();
        if (viewModel.Firmware.Blocked) { SetPanelMode(SidePanelMode.Settings); }
        await UpdateInputVisibilityAsync();
        await ResumeLiveAfterRecoveryAsync();
        var firstLaunch = !launchInitialized;
        launchInitialized = true;
        if (firstLaunch) { _ = CheckForAppUpdatesAsync(manual: false); }
        _ = CheckDeviceDiagnosticsAsync();
    }
    private async Task InitializeRuntimeAsync()
    {
        runtime = new(engine, source, desktop.Runtime!, buttons, desktop.Log);
        await runtime.ResumeAsync();
        viewModel.SourceName = "Live · FEEL-VR pedals";
        viewModel.ShowMessage("Pedal input and virtual output start automatically. Select a profile or tune the pedals.");
    }

    private async Task InitializeDesktopCoreAsync()
    {
        try
        {
            preferences = await desktop.Preferences.LoadAsync(lifetime.Token);
            preferences = preferences with { StartWithWindows = desktop.Startup.IsEnabled };
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { desktop.Log?.Write("settings", "An operation needs attention.", failure); if (!closing) { viewModel.Preferences.Status = $"Some settings could not be read: {failure.Message}"; } }
        if (closing) { return; }
        viewModel.Preferences.Load(preferences);
        Localization.Localizer.Instance.Language = preferences.Language;
        preferencesLoaded = true;
        try
        {
            tray = desktop.CreateTray(GetTrayState);
            tray.CommandRequested += TrayCommandRequested;
            tray.ProfileRequested += TrayProfileRequested;
            var handle = new WindowInteropHelper(this).EnsureHandle();
            activationSource = HwndSource.FromHwnd(handle);
            activationSource?.AddHook(ActivationMessage);
            if (activationSource is not null && desktop.Buttons is not null)
            {
                buttons.Dispose();
                try { buttons = desktop.Buttons(activationSource); }
                catch (System.ComponentModel.Win32Exception failure)
                { buttons = new ButtonStateBuffer(); viewModel.Preferences.Status = $"Button binding unavailable: {failure.Message}"; }
            }
            if (Application.Current is { } application) { application.SessionEnding += SessionEnding; }
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        {
            desktop.Log?.Write("settings", "An operation needs attention.", failure);
            tray?.Dispose(); tray = null;
            viewModel.Preferences.Status = $"Tray unavailable; closing will exit normally. {failure.Message}";
        }
        UpdateRefreshPolicy();
    }

    internal TrayMenuState GetTrayState() => new(viewModel.ProfileTabs.ToArray(), viewModel.SelectedProfile, viewModel.PanelMode,
        viewModel.CanEdit && !closing, viewModel.CanTune && !closing);

    private async void SettingsModeClick(object sender, RoutedEventArgs e) => await RequestPanelModeAsync(SidePanelMode.Settings);
    private async void HideToTrayClick(object sender, RoutedEventArgs e) => await HideToTrayAsync();
    private void ExitAppClick(object sender, RoutedEventArgs e) => RequestExit();
    private async void PreferenceChanged(object sender, RoutedEventArgs e) => await SavePreferencesAsync();
    private async void LanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (preferencesLoaded && viewModel.Preferences.Language != preferences.Language) { await SavePreferencesAsync(); }
        RefreshSettingsSearch();
    }

    internal Task SavePreferencesAsync()
    {
        if (closing) { return settingsOperation ?? Task.CompletedTask; }
        savePreferencesAgain = true;
        if (settingsOperation is { IsCompleted: false }) { return settingsOperation; }
        return settingsOperation = SavePreferencesCoreAsync();
    }
    private async Task SavePreferencesCoreAsync()
    {
        viewModel.Preferences.Saving = true;
        try
        {
            do
            {
                savePreferencesAgain = false;
                var next = viewModel.Preferences.Snapshot();
                // Finish requested preference writes on Exit; acquisition has a separate cancellation path.
                await desktop.Preferences.SaveAsync(next, CancellationToken.None);
                try
                {
                    if (next.StartWithWindows != preferences.StartWithWindows)
                    { await Task.Run(() => desktop.Startup.SetEnabled(next.StartWithWindows)); }
                }
                catch
                {
                    await desktop.Preferences.SaveAsync(preferences, CancellationToken.None);
                    throw;
                }
                preferences = next;
                Localization.Localizer.Instance.Language = preferences.Language;
                await UpdateInputVisibilityAsync();
            } while (savePreferencesAgain);
            viewModel.Preferences.Status = "Settings saved.";
            UpdateRefreshPolicy();
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        {
            desktop.Log?.Write("settings", "An operation needs attention.", failure);
            savePreferencesAgain = false;
            viewModel.Preferences.Load(preferences);
            if (!closing) { viewModel.Preferences.Status = $"Settings were not changed: {failure.Message}"; }
        }
        finally { viewModel.Preferences.Saving = false; }
    }

    private Task UpdateInputVisibilityAsync()
    {
        if (closing || !preferencesLoaded || desktop.InputVisibility is null) { return Task.CompletedTask; }
        return visibilityOperation = UpdateInputVisibilityCoreAsync();
    }
    private async Task UpdateInputVisibilityCoreAsync()
    {
        try
        {
            var result = await desktop.InputVisibility!.ConfigureAsync(preferences.HidePhysicalPedals, lifetime.Token);
            viewModel.Preferences.InputVisibilityStatus = result.Detail;
            desktop.Log?.Write("input-visibility", result.Detail);
        }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        {
            desktop.Log?.Write("input-visibility", "Could not update physical pedal visibility.", failure);
            if (!closing) { viewModel.Preferences.InputVisibilityStatus = $"Physical pedal visibility needs attention: {failure.Message}"; }
        }
    }

    private async void TrayCommandRequested(TrayCommand command)
    {
        if (command == TrayCommand.Exit) { RequestExit(); return; }
        if (command == TrayCommand.ViewUpdate) { OpenAppRelease(); return; }
        RestoreFromTray();
        if (command == TrayCommand.Tune) { await RequestPanelModeAsync(SidePanelMode.Tuning); }
        else if (command == TrayCommand.Calibrate) { await RequestPanelModeAsync(SidePanelMode.Calibration); }
        else if (command == TrayCommand.Settings) { await RequestPanelModeAsync(SidePanelMode.Settings); }
    }
    private void TrayProfileRequested(ProfileTab profile)
    {
        if (!GetTrayState().CanSwitchProfile || !viewModel.ProfileTabs.Contains(profile)) { return; }
        Run(() => { StopHold(); viewModel.SwitchProfile(profile); });
    }

    internal void RestoreFromTray()
    {
        if (closing || disposed) { return; }
        Show();
        if (WindowState == WindowState.Minimized) { WindowState = restoreState; }
        Activate();
        UpdateRefreshPolicy();
    }
    internal async Task<bool> HideToTrayAsync()
    {
        if (tray is null || closing || disposed || hidingToTray) { return false; }
        hidingToTray = true;
        try
        {
            if (viewModel.Tour.IsOpen) { await FinishTourAsync(restorePanel: false); }
            if (hardwareSession is not null || WantsCalibration || modeOperation is { IsCompleted: false })
            {
                await RequestPanelModeAsync(SidePanelMode.Tuning);
                if (modeOperation is { } pending) { await pending; }
                if (hardwareSession is not null || viewModel.IsCalibrationMode || closing) { RestoreFromTray(); return false; }
            }
            StopHold();
            Hide();
            UpdateRefreshPolicy();
            return true;
        }
        finally { hidingToTray = false; }
    }
    private async void WindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) { restoreState = WindowState; }
        else if (preferences.MinimizeToTray && tray is not null && !closing)
        {
            if (!await HideToTrayAsync() && !closing) { RestoreFromTray(); }
        }
        UpdateRefreshPolicy();
    }
    private void UpdateRefreshPolicy()
    {
        if (closing || disposed) { return; }
        if (preferences.PauseGraphWhenHidden && (!IsVisible || WindowState == WindowState.Minimized)) { refresh.Stop(); }
        else { refresh.Start(); }
    }
    internal void RequestExit() { exitRequested = true; Close(); }
    private void SessionEnding(object sender, SessionEndingCancelEventArgs e) => exitRequested = true;
    private nint ActivationMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (InstanceActivation.Message != 0 && message == InstanceActivation.Message) { RestoreFromTray(); handled = true; }
        return 0;
    }
}
