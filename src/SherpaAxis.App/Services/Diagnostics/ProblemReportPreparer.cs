using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using SherpaAxis.App.Models;

namespace SherpaAxis.App.Services.Diagnostics;

internal sealed record PreparedProblemReport(string ArchivePath, string Markdown, Uri IssuePage);

internal sealed class ProblemReportPreparer(DiagnosticLog log, string directory)
{
    internal async Task<PreparedProblemReport> PrepareAsync(IReadOnlyDictionary<string, string> status, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var name = "SherpaAxis-diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8] + ".zip";
        var path = Path.Combine(directory, name);
        var snapshot = status.ToDictionary(pair => pair.Key, pair => pair.Value);
        var version = typeof(ProblemReportPreparer).Assembly.GetName().Version!.ToString(3);
        var markdown = BuildMarkdown(version, name, snapshot);
        var issue = new Uri(ApplicationLinks.Issues.AbsoluteUri + "/new?title=" + Uri.EscapeDataString("Bug report (v" + version + ")")
            + "&body=" + Uri.EscapeDataString(markdown));
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            System.IO.Directory.CreateDirectory(directory);
            new DiagnosticReportExporter(log).Export(path, snapshot, markdown);
        }, token);
        token.ThrowIfCancellationRequested();
        return new(path, markdown, issue);
    }

    private static string BuildMarkdown(string version, string archiveName, Dictionary<string, string> status)
    {
        string Value(string key) => status.TryGetValue(key, out var value) ? Short(value) : "Unavailable";
        return $"""
            ## What happened?
            Describe the problem here.

            ## Steps to reproduce
            1.
            2.

            ## Expected behaviour
            What did you expect to happen?

            ## Technical details
            - SherpaAxis: v{version}
            - OS: {RuntimeInformation.OSDescription}
            - Architecture: {RuntimeInformation.ProcessArchitecture}
            - Runtime: {RuntimeInformation.FrameworkDescription}
            - Input: {Value("input")}
            - Virtual output: {Value("output")}
            - Calibration recovery blocked: {Value("calibrationRecoveryBlocked")}
            - Firmware review blocked: {Value("firmwareReviewBlocked")}
            - Firmware operation running: {Value("firmwareOperationRunning")}

            ## Diagnostics
            Review and attach `{archiveName}` before submitting this issue.
            The ZIP was saved locally and includes report.json, recent application logs and this draft as issue.md.
            """;
    }

    private static string Short(string text)
    {
        var safe = DiagnosticLog.Sanitize(text).Replace('\r', ' ').Replace('\n', ' ').Replace('`', '\'');
        if (safe.Length <= 96) { return safe; }
        var length = char.IsHighSurrogate(safe[95]) ? 95 : 96;
        return safe[..length] + "…";
    }
}
