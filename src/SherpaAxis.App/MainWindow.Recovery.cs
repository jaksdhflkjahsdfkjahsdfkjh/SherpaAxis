using System.Diagnostics;
using System.Windows;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private Task? recoveryCheck;
    private Task CheckStartupRecoveryAsync() => recoveryCheck is { IsCompleted: false } ? recoveryCheck : recoveryCheck = CheckStartupRecoveryCoreAsync();
    private async Task CheckStartupRecoveryCoreAsync()
    {
        if (desktop.Recovery is null) { return; }
        viewModel.Recovery.Checking();
        try
        {
            if (runtime is not null) { await runtime.PauseAsync(); }
            viewModel.Recovery.Apply(await desktop.Recovery.CheckAsync(lifetime.Token));
        }
        catch (Exception failure) when (IsExpected(failure)) { desktop.Log?.Write("recovery", "An operation needs attention.", failure); viewModel.Recovery.Fail(failure.Message); }
    }

    private async Task ResumeLiveAfterRecoveryAsync()
    {
        if (closing || viewModel.Recovery.IsBlocked || viewModel.Firmware.Blocked || viewModel.Firmware.Running || hardwareSession is not null || desktop.Runtime is null) { return; }
        if (runtime is null) { await (runtimeInitialization ??= InitializeRuntimeAsync()); }
        else if (runtime.IsPaused)
        {
            await runtime.ResumeAsync();
            viewModel.SourceName = "Live · FEEL-VR pedals";
        }
    }

    private async void CheckRecoveryClick(object sender, RoutedEventArgs e) => await ResolveStartupRecoveryAsync(false, false);
    private async void RestoreRecoveryClick(object sender, RoutedEventArgs e) => await ResolveStartupRecoveryAsync(true, false);
    private async void SaveRecoveryClick(object sender, RoutedEventArgs e) => await ResolveStartupRecoveryAsync(false, true);
    internal Task ResolveStartupRecoveryAsync(bool restore, bool save) => RunAsync(async () =>
    {
        if (desktop.Recovery is null || hardwareSession is not null || viewModel.IsSwitchingMode) { return; }
        var selected = viewModel.Recovery.Selected;
        viewModel.Recovery.Checking();
        string? error = null;
        try
        {
            if (runtime is not null) { await runtime.PauseAsync(); }
            if (restore && selected is not null) { await desktop.Recovery.RestoreAsync(selected.Path, lifetime.Token); }
            if (save && selected is not null) { await desktop.Recovery.SaveRestoredAsync(selected.Path, lifetime.Token); }
        }
        catch (Exception failure) when (IsExpected(failure)) { desktop.Log?.Write("recovery", "An operation needs attention.", failure); error = failure.Message; }
        await CheckStartupRecoveryAsync();
        if (error is not null) { viewModel.Recovery.Status = $"Recovery action failed: {error}"; }
        if (!closing && !viewModel.Recovery.IsBlocked)
        {
            await ResumeLiveAfterRecoveryAsync();
            viewModel.ShowMessage("Original controller state verified. Automatic pedal input and output can resume.");
        }
    });

    private void OpenRecoveryFolderClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (desktop.Recovery is null) { return; }
        try { _ = Process.Start(new ProcessStartInfo(desktop.Recovery.DirectoryPath) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception failure) { throw new System.IO.IOException($"Could not open recovery folder: {failure.Message}", failure); }
    });
}
