using System.Windows;
using SherpaAxis.App.Services.Diagnostics;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private async void ReportProblemClick(object sender, RoutedEventArgs e) => await PrepareProblemReportAsync();

    internal Task PrepareProblemReportAsync() => RunAsync(async () =>
    {
        if (desktop.Log is null || desktop.ProblemReports is not { } support)
        { throw new InvalidOperationException("Problem reporting is unavailable in this build."); }
        var prepared = await new ProblemReportPreparer(desktop.Log, support.Directory).PrepareAsync(CaptureDiagnosticStatus(), lifetime.Token);
        if (closing) { return; }
        string? openingError = null;
        try { support.RevealArchive(prepared.ArchivePath); }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { openingError = "Could not open the report folder."; desktop.Log.Write("support", openingError, failure); }
        try { support.OpenIssue(prepared.IssuePage); }
        catch (Exception failure) when (OperationErrors.IsExpected(failure))
        { openingError = "Could not open GitHub in the browser."; desktop.Log.Write("support", openingError, failure); }
        viewModel.ShowMessage(openingError is null
            ? $"Diagnostic ZIP ready: {prepared.ArchivePath}. Review it and attach it to the GitHub issue."
            : $"{openingError} Diagnostic ZIP saved: {prepared.ArchivePath}. You can share it manually.");
    });
}
