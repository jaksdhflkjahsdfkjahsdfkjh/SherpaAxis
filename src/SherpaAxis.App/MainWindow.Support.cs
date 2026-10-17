using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SherpaAxis.App.Services;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private ProfileDraftSession? pendingDrafts;
    private readonly DispatcherTimer draftTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Task draftWrite = Task.CompletedTask;
    private string? draftCandidate;
    private string? draftWritten;
    private long draftChangedAt;
    private bool draftsInitialized;

    private async Task InitializeDraftsAsync()
    {
        if (desktop.Drafts is null) { return; }
        try
        {
            pendingDrafts = await desktop.Drafts.LoadAsync(lifetime.Token);
            viewModel.HasRecoverableDrafts = pendingDrafts?.Profiles.Count > 0;
            draftsInitialized = true;
            draftTimer.Tick += DraftTick;
            draftTimer.Start();
        }
        catch (Exception failure) when (IsExpected(failure))
        {
            // Preserve an unreadable recovery file instead of replacing it with an empty session.
            desktop.Log?.Write("draft-recovery", "Could not read unfinished edits.", failure);
            viewModel.ShowMessage($"Draft recovery unavailable: {failure.Message}");
        }
    }
    private void DraftTick(object? sender, EventArgs e)
    {
        if (closing || !draftWrite.IsCompleted) { return; }
        var snapshot = CaptureDraftSession();
        var text = JsonSerializer.Serialize(snapshot);
        if (text != draftCandidate) { draftCandidate = text; draftChangedAt = Environment.TickCount64; return; }
        if (text == draftWritten || Environment.TickCount64 - draftChangedAt < 750) { return; }
        draftWrite = WriteDraftsAsync(snapshot, text);
    }
    private ProfileDraftSession CaptureDraftSession()
    {
        var current = viewModel.CaptureDrafts();
        return new(1, (pendingDrafts?.Profiles ?? []).Concat(current.Profiles).GroupBy(profile => profile.Id).Select(group => group.Last()).ToArray());
    }
    private async Task<bool> WriteDraftsAsync(ProfileDraftSession snapshot, string text)
    {
        if (desktop.Drafts is null) { return true; }
        try { await desktop.Drafts.SaveAsync(snapshot); draftWritten = text; return true; }
        catch (Exception failure) when (IsExpected(failure))
        {
            desktop.Log?.Write("draft-save", "Could not preserve unfinished edits.", failure);
            viewModel.ShowMessage($"Unsaved edits could not be backed up: {failure.Message}");
            return false;
        }
    }
    internal async Task FlushDraftsAsync(bool requireBackup = false)
    {
        if (desktop.Drafts is null || libraryInitialization is null) { return; }
        const string failure = "Could not preserve unsaved edits. Save the profiles before exiting.";
        if (!draftsInitialized)
        {
            if (requireBackup && viewModel.CaptureDrafts().Profiles.Count > 0) { throw new IOException(failure); }
            return;
        }
        await draftWrite;
        var snapshot = CaptureDraftSession();
        var saved = await WriteDraftsAsync(snapshot, JsonSerializer.Serialize(snapshot));
        if (!saved && requireBackup && snapshot.Profiles.Count > 0) { throw new IOException(failure); }
    }
    private async void RestoreDraftsClick(object sender, RoutedEventArgs e) => await RestoreDraftsAsync();
    internal Task RestoreDraftsAsync() => RunAsync(async () =>
    {
        if (pendingDrafts is null) { return; }
        StopHold();
        viewModel.RestoreDrafts(pendingDrafts, library.DirectoryPath);
        pendingDrafts = null;
        viewModel.SetAutoLoadPath(autoLoadProfilePath);
        await FlushDraftsAsync();
    });
    private async void DiscardDraftsClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        pendingDrafts = null;
        viewModel.HasRecoverableDrafts = false;
        await FlushDraftsAsync();
        viewModel.ShowMessage("Previous unsaved drafts discarded. Saved profiles are unchanged.");
    });

    private async void ExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Localization.Localizer.Translate("Export diagnostic report"),
            Filter = "ZIP (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = "SherpaAxis-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".zip"
        };
        if (dialog.ShowDialog(this) == true) { await RunAsync(() => ExportDiagnosticsAsync(dialog.FileName)); }
    }
    internal Task ExportDiagnosticsAsync(string path)
    {
        var status = new Dictionary<string, string>
        {
            ["input"] = viewModel.Status,
            ["output"] = viewModel.OutputInfo,
            ["calibrationRecoveryBlocked"] = viewModel.Recovery.IsBlocked.ToString(),
            ["firmwareReviewBlocked"] = viewModel.Firmware.Blocked.ToString(),
            ["firmwareOperationRunning"] = viewModel.Firmware.Running.ToString(),
            ["language"] = viewModel.Preferences.Language
        };
        foreach (var check in viewModel.Diagnostics.Checks) { status[check.Title] = check.Detail; }
        return ExportCoreAsync();
        async Task ExportCoreAsync()
        {
            if (desktop.Log is null) { throw new InvalidOperationException("Local diagnostic logging is unavailable."); }
            await Task.Run(() => desktop.Log.Export(path, status));
            viewModel.ShowMessage("Diagnostic report exported. Review the ZIP before sharing it.");
        }
    }
}
