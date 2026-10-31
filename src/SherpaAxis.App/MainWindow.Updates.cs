using System.Net.Http;
using System.Text.Json;
using System.Windows;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.Services.Updates;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private Task? updateCheck;
    private Task? appUpdateOperation;
    private Task<PreparedAppUpdate>? appUpdateDownload;
    private CancellationTokenSource? appUpdateCancellation;

    internal Task CheckForAppUpdatesAsync(bool manual = true)
    {
        if (closing || desktop.Updates is null || viewModel.Updates.IsUpdating || !manual && !preferences.CheckForUpdates) { return Task.CompletedTask; }
        return updateCheck is { IsCompleted: false } ? updateCheck : updateCheck = CheckForAppUpdatesCoreAsync(manual);
    }
    private async Task CheckForAppUpdatesCoreAsync(bool manual)
    {
        viewModel.Updates.Busy = true;
        viewModel.Updates.Status = "Checking for updates…";
        try
        {
            var release = await desktop.Updates!.CheckAsync(lifetime.Token);
            if (closing) { return; }
            viewModel.Updates.Apply(release);
            if (release is not null && viewModel.Updates.HasUpdate && tray is not null && (manual || preferences.CheckForUpdates)
                && (!IsVisible || WindowState == WindowState.Minimized)
                && await desktop.Updates.MarkNotifiedAsync(release, lifetime.Token) && !closing)
            { tray?.NotifyUpdate(release.DisplayVersion); }
        }
        catch (OperationCanceledException) when (closing || lifetime.IsCancellationRequested) { }
        catch (Exception failure) when (OperationErrors.IsExpected(failure) || failure is HttpRequestException or JsonException)
        {
            desktop.Log?.Write("app-update", "Could not check for a new release.", failure);
            if (!closing) { viewModel.Updates.Status = "Could not check for updates. Check your connection and try again later."; }
        }
        finally { viewModel.Updates.Busy = false; }
    }
    private async void CheckAppUpdatesClick(object sender, RoutedEventArgs e) => await CheckForAppUpdatesAsync();
    private void OpenAppReleaseClick(object sender, RoutedEventArgs e) => OpenAppRelease();
    private async void InstallAppUpdateClick(object sender, RoutedEventArgs e) => await InstallAvailableAppUpdateAsync();
    private async void AppReleaseNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) { e.Handled = true; await InstallAvailableAppUpdateAsync(); }
    private void AppUpdateLogsClick(object sender, RoutedEventArgs e)
    { System.IO.Directory.CreateDirectory(AppUpdateDownloader.DefaultDirectory); OpenDiagnosticTool(AppUpdateDownloader.DefaultDirectory); }

    internal Task InstallAvailableAppUpdateAsync()
    {
        if (appUpdateOperation is { IsCompleted: false })
        {
            if (!viewModel.Updates.IsInstalling) { appUpdateCancellation?.Cancel(); }
            return appUpdateOperation;
        }
        return appUpdateOperation = InstallAvailableAppUpdateCoreAsync();
    }

    private bool CanInstallAppUpdateNow => !closing && !disposed && viewModel.CanTune && hardwareSession is null
        && !viewModel.Firmware.Running && !viewModel.Firmware.Blocked && !viewModel.Recovery.IsBlocked;

    private async Task InstallAvailableAppUpdateCoreAsync()
    {
        if (viewModel.Updates.Release is not { } release || !viewModel.Updates.CanAct || closing || disposed) { return; }
        if (!CanInstallAppUpdateNow)
        { viewModel.ShowMessage("Finish calibration or firmware recovery before updating the app."); return; }
        if (desktop.AppUpdates is not { } updater)
        { viewModel.Updates.Status = "Automatic updating is unavailable in this build. Open the release page to update manually."; return; }
        var locked = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        appUpdateCancellation = cancellation;
        viewModel.Updates.IsUpdating = true;
        viewModel.Updates.Percent = null;
        viewModel.Updates.Status = "Checking update files…";
        try
        {
            VendorControl.RequireClosed(desktop.VendorControlRunning);
            var installation = updater.Installation();
            var progress = new Progress<AppUpdateProgress>(value =>
            {
                if (!closing && ReferenceEquals(appUpdateCancellation, cancellation) && viewModel.Updates.IsUpdating && !viewModel.Updates.IsInstalling)
                { viewModel.Updates.Percent = value.Percent; viewModel.Updates.Status = value.Percent is { } amount ? $"{value.Stage} {amount}%" : value.Stage; }
            });
            appUpdateDownload = updater.Prepare(release, installation, progress, cancellation.Token);
            var package = await appUpdateDownload;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!CanInstallAppUpdateNow) { throw new InvalidOperationException("Finish calibration or firmware recovery before updating the app."); }
            await FlushDraftsAsync(requireBackup: true);
            viewModel.CanEdit = false;
            locked = true;
            viewModel.Updates.IsInstalling = true;
            viewModel.Updates.Status = "Installing update. SherpaAxis will close and reopen automatically.";
            await using var handoff = await updater.Start(package, preferences.Language, cancellation.Token);
            await DisposeAsync();
            handoff.Commit();
            canClose = true;
            Close();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!closing) { viewModel.Updates.Status = "Update download cancelled."; } }
        catch (Exception failure) when (OperationErrors.IsExpected(failure) || failure is HttpRequestException or JsonException)
        {
            desktop.Log?.Write("app-update", "Automatic app update could not finish.", failure);
            if (!disposed)
            { viewModel.Updates.Status = $"Could not update the app: {failure.Message}"; viewModel.ShowMessage(viewModel.Updates.Status); }
        }
        finally
        {
            appUpdateCancellation = null;
            viewModel.Updates.IsInstalling = false;
            viewModel.Updates.IsUpdating = false;
            viewModel.Updates.Percent = null;
            if (locked && !closing && !disposed) { viewModel.CanEdit = true; }
        }
    }

    internal void ShowAppUpdateResult(AppUpdateResult result)
    {
        var success = result.Success && AppUpdateChecker.TryVersion(result.Version, out var version)
            && version == AppUpdateDownloader.Normalize(typeof(MainWindow).Assembly.GetName().Version!);
        desktop.Log?.Write("app-update", result.Detail);
        var message = success ? $"Updated to {ViewModels.DashboardViewModel.VersionLabel}."
            : "The app update did not finish. SherpaAxis was reopened. Open update logs for details.";
        viewModel.Updates.Status = message;
        viewModel.ShowMessage(message);
    }
    private void OpenAppRelease()
    {
        if (viewModel.Updates.ReleasePage is { } page) { OpenDiagnosticTool(page.AbsoluteUri); }
    }
}
