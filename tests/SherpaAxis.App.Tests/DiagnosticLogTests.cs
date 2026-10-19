using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SherpaAxis.App.Services;
using SherpaAxis.App.Services.Diagnostics;

namespace SherpaAxis.App.Tests;

public sealed class DiagnosticLogTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1023)]
    [InlineData(4194305)]
    public void InvalidLogLimitsFailAtConstruction(int maximumBytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticLog(Path.GetTempPath(), maximumBytes));

    [Fact]
    public void EscapedUnicodeMessagesRespectTheByteLimitAndRemainValidJson()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-unicode-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagnosticLog(folder, 1024);
            log.Write(new string('漢', 64), new string('漢', 1000));
            var path = Assert.Single(Directory.GetFiles(folder));
            Assert.InRange(new FileInfo(path).Length, 1, 1024);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(json.RootElement.GetProperty("detail").GetString()!.Length > 0);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }
    [Fact]
    public void LogsRotateStayBoundedAndSuppressRepeatedErrors()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagnosticLog(folder, 4096);
            log.Write("test", "repeat"); log.Write("test", "repeat");
            Assert.Single(File.ReadAllLines(Path.Combine(folder, "app.jsonl")));
            for (var i = 0; i < 100; i++) { log.Write("failure", i + new string('x', 1000)); }
            var files = Directory.GetFiles(folder);
            Assert.Equal(3, files.Length);
            foreach (var file in files)
            {
                Assert.InRange(new FileInfo(file).Length, 1, 4096);
                foreach (var line in File.ReadAllLines(file)) { using var json = JsonDocument.Parse(line); Assert.True(json.RootElement.TryGetProperty("utc", out _)); }
            }
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public void ExportContainsRedactedStatusAndLogsWithoutOtherLocalFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var log = new DiagnosticLog(Path.Combine(folder, "Logs"));
            log.Write("input", @"PE0017003200485656510A20383938 failed", new IOException(@"Could not open C:\Users\Private Name\profile.json"));
            File.WriteAllText(Path.Combine(log.DirectoryPath, "secret-profile.json"), "private settings");
            File.WriteAllText(Path.Combine(log.DirectoryPath, "recording.csv"), "pedal recording");
            var report = Path.Combine(folder, "support.zip");
            new DiagnosticReportExporter(log).Export(report, new Dictionary<string, string> { ["input"] = @"PE0017003200485656510A20383938 C:\Users\Private Name\device", ["output"] = "vJoy 2 ready" });
            using var zip = ZipFile.OpenRead(report);
            Assert.Equal(["logs/app.jsonl", "report.json"], zip.Entries.Select(entry => entry.FullName).Order().ToArray());
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                Assert.DoesNotContain("Private Name", text, StringComparison.Ordinal);
                Assert.DoesNotContain("PE0017003200485656510A20383938", text, StringComparison.Ordinal);
                Assert.DoesNotContain("private settings", text, StringComparison.Ordinal);
                Assert.Contains("[controller]", text, StringComparison.Ordinal);
                Assert.Contains("[path]", text, StringComparison.Ordinal);
            }
            using var statusReader = new StreamReader(zip.GetEntry("report.json")!.Open());
            using var status = JsonDocument.Parse(statusReader.ReadToEnd());
            Assert.Equal("vJoy 2 ready", status.RootElement.GetProperty("status").GetProperty("output").GetString());
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public void UnwritableLogDoesNotBreakTheAppAndFailedExportLeavesExistingReport()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-log-failure-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var blocked = Path.Combine(folder, "blocked"); File.WriteAllText(blocked, "keep");
            var log = new DiagnosticLog(blocked);
            log.Write("test", "failure"); Assert.NotNull(log.Failure);
            var report = Path.Combine(folder, "existing.zip"); File.WriteAllText(report, "original");
            using (var locked = new FileStream(report, FileMode.Open, FileAccess.Read, FileShare.None))
            { Assert.True(Record.Exception(() => new DiagnosticReportExporter(log).Export(report, new Dictionary<string, string>())) is IOException or UnauthorizedAccessException); }
            Assert.Equal("original", File.ReadAllText(report));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }
}
