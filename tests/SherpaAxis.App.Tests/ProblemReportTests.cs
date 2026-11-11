using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Diagnostics;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class ProblemReportTests
{
    [Fact]
    public async Task PreparedZipAndIssueUseSanitizedTechnicalDetailsAndNeverIncludeOtherLocalFiles()
    {
        var folder = Temporary();
        try
        {
            var log = new DiagnosticLog(Path.Combine(folder, "Logs"));
            log.Write("input", @"PE0017003200485656510A20383938 failed at C:\Users\Private Name\device");
            File.WriteAllText(Path.Combine(log.DirectoryPath, "profile.json"), "private settings");
            File.WriteAllText(Path.Combine(log.DirectoryPath, "recording.csv"), "raw pedal recording");
            var report = await new ProblemReportPreparer(log, Path.Combine(folder, "Reports")).PrepareAsync(
                new Dictionary<string, string> { ["input"] = @"PE0017003200485656510A20383938 C:\Users\Private Name\device", ["output"] = "vJoy ready" }, default);
            Assert.True(File.Exists(report.ArchivePath));
            Assert.Equal(ApplicationLinks.Issues.Host, report.IssuePage.Host);
            Assert.Equal(ApplicationLinks.Issues.AbsolutePath + "/new", report.IssuePage.AbsolutePath);
            var query = Uri.UnescapeDataString(report.IssuePage.Query);
            Assert.Contains("Bug report (v" + typeof(MainWindow).Assembly.GetName().Version!.ToString(3) + ")", query, StringComparison.Ordinal);
            Assert.Contains(report.Markdown, query, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(report.ArchivePath), report.Markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(folder, report.Markdown, StringComparison.Ordinal);
            using var zip = ZipFile.OpenRead(report.ArchivePath);
            Assert.Equal(["issue.md", "logs/app.jsonl", "report.json"], zip.Entries.Select(entry => entry.FullName).Order().ToArray());
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open()); var content = reader.ReadToEnd();
                Assert.DoesNotContain("Private Name", content, StringComparison.Ordinal);
                Assert.DoesNotContain("PE0017003200485656510A20383938", content, StringComparison.Ordinal);
                Assert.DoesNotContain("private settings", content, StringComparison.Ordinal);
                Assert.DoesNotContain("raw pedal recording", content, StringComparison.Ordinal);
            }
            using var issueReader = new StreamReader(zip.GetEntry("issue.md")!.Open());
            Assert.Equal(report.Markdown, issueReader.ReadToEnd());
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(report.ArchivePath)!, "*.tmp"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task LongUnicodeStatusStaysInsideABrowserUrlAndRepeatedReportsHaveUniqueArchives()
    {
        var folder = Temporary();
        try
        {
            var prepare = new ProblemReportPreparer(new(Path.Combine(folder, "Logs")), Path.Combine(folder, "Reports"));
            var longText = string.Concat(Enumerable.Repeat("🚗漢字?&title=other`\n", 300));
            var status = new Dictionary<string, string> { ["input"] = longText, ["output"] = longText, ["firmwareReviewBlocked"] = longText };
            var reports = await Task.WhenAll(prepare.PrepareAsync(status, default), prepare.PrepareAsync(status, default));
            Assert.NotEqual(reports[0].ArchivePath, reports[1].ArchivePath);
            Assert.All(reports, report => Assert.InRange(report.IssuePage.AbsoluteUri.Length, 100, 7500));
            Assert.All(reports, report => Assert.True(File.Exists(report.ArchivePath)));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task CancellationAndExportFailureNeverProduceAnIncompleteZip()
    {
        var folder = Temporary();
        try
        {
            var target = Path.Combine(folder, "Reports"); var log = new DiagnosticLog(Path.Combine(folder, "Logs"));
            var prepare = new ProblemReportPreparer(log, target);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare.PrepareAsync(new Dictionary<string, string>(), cancelled.Token));
            Assert.False(Directory.Exists(target));
            File.WriteAllText(target, "original");
            await Assert.ThrowsAnyAsync<IOException>(() => prepare.PrepareAsync(new Dictionary<string, string>(), default));
            Assert.Equal("original", File.ReadAllText(target));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task BothUiEntryPointsPrepareTheArchiveBeforeOpeningTheDraftWithoutConnectingHardware(string language)
        => HardwarePedalUiTests.OnUiAsync(async () =>
        {
            var folder = Temporary();
            try
            {
                var opened = new List<Uri>(); var revealed = new List<string>(); var connections = 0;
                var desktop = new SettingsAndTrayTests.FakeDesktop(); desktop.Store.Value = new(Language: language);
                var services = desktop.Services with
                {
                    Log = new(Path.Combine(folder, "Logs")),
                    ProblemReports = new(Path.Combine(folder, "Reports"),
                    page => { Assert.True(File.Exists(revealed[^1])); opened.Add(page); }, path => { Assert.True(File.Exists(path)); revealed.Add(path); })
                };
                var engine = new DashboardEngine();
                await using var main = new MainWindow(engine, (_, _, _) => { connections++; throw new InvalidOperationException("Support must not open a controller."); }, services, new(Path.Combine(folder, "Profiles")));
                await main.InitializeForLaunchAsync();
                await main.RequestPanelModeAsync(SidePanelMode.Settings);
                Localizer.Instance.Language = language;
                engine.Begin(new("live-device-axis"));
                engine.Accept(new(SherpaAxis.FeelVR.PedalConnectionState.Connected, TimeSpan.Zero, 1, new(100, 600, 300)));
                var before = engine.Snapshot(SherpaAxis.Profiles.PedalAxis.Brake).Connection;
                var button = Assert.IsType<Button>(main.FindName("ReportProblemButton"));
                Assert.Equal(Localizer.Translate("Report a problem…"), button.Content);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => opened.Count == 1);
                var link = Assert.IsType<Hyperlink>(main.FindName("BugReportLink"));
                var request = new RequestNavigateEventArgs(ApplicationLinks.Issues, "") { RoutedEvent = Hyperlink.RequestNavigateEvent };
                link.RaiseEvent(request);
                await UntilAsync(() => opened.Count == 2);
                Assert.True(request.Handled); Assert.Equal(2, revealed.Count); Assert.Equal(0, connections);
                Assert.NotEqual(revealed[0], revealed[1]);
                Assert.Equal(before, engine.Snapshot(SherpaAxis.Profiles.PedalAxis.Brake).Connection);
                Assert.True(((DashboardViewModel)main.DataContext).CanEdit);
                Assert.All(opened, page => Assert.StartsWith(ApplicationLinks.Issues.AbsoluteUri + "/new?", page.AbsoluteUri, StringComparison.Ordinal));
            }
            finally { Localizer.Instance.Language = "en"; Directory.Delete(folder, true); }
        });

    [Fact]
    public Task BrowserFailureKeepsTheReportAndFolderFailureCanBeRetried() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = Temporary();
        try
        {
            var location = Path.Combine(folder, "Reports"); File.WriteAllText(location, "blocked");
            var calls = 0; string? archive = null;
            var desktop = new SettingsAndTrayTests.FakeDesktop();
            await using var main = new MainWindow(new(), desktopServices: desktop.Services with
            { Log = new(Path.Combine(folder, "Logs")), ProblemReports = new(location, _ => { calls++; throw new System.ComponentModel.Win32Exception("No browser"); }, path => archive = path) }, profileLibrary: new(Path.Combine(folder, "Profiles")));
            await main.InitializeForLaunchAsync();
            await main.PrepareProblemReportAsync(); Assert.Equal(0, calls); Assert.Null(archive);
            Assert.Equal("blocked", File.ReadAllText(location));
            File.Delete(location);
            await main.PrepareProblemReportAsync();
            Assert.Equal(1, calls); Assert.NotNull(archive); Assert.True(File.Exists(archive));
            using var zip = ZipFile.OpenRead(archive); Assert.NotNull(zip.GetEntry("issue.md"));
            Assert.Contains("Could not open GitHub", ((DashboardViewModel)main.DataContext).Message, StringComparison.Ordinal);
            Assert.True(((DashboardViewModel)main.DataContext).CanEdit);
        }
        finally { Directory.Delete(folder, true); }
    });

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) { await Task.Delay(5, timeout.Token); }
    }
    private static string Temporary()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-problem-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); return folder;
    }
}
