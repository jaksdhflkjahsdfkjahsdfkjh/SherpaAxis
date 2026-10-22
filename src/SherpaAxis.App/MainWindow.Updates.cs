using System.Net.Http;
using System.Text.Json;
using System.Windows;
using SherpaAxis.App.Services.Diagnostics;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private Task? updateCheck;

    internal Task CheckForAppUpdatesAsync(bool manual = true)
    {
        if (closing || desktop.Updates is null || !manual && !preferences.CheckForUpdates) { return Task.CompletedTask; }
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
    private void AppReleaseNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) { e.Handled = true; OpenAppRelease(); }
    private void OpenAppRelease()
    {
        if (viewModel.Updates.ReleasePage is { } page) { OpenDiagnosticTool(page.AbsoluteUri); }
    }
}
