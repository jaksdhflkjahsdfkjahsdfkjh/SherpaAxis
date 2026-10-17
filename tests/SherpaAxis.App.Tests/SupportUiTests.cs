using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class SupportUiTests
{
    [Fact]
    public Task SettingsErrorsReachTheExportedReportWithNoProfileContents() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-support-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var desktop = new SettingsAndTrayTests.FakeDesktop();
            var log = new DiagnosticLog(Path.Combine(folder, "Logs"));
            await using var main = new MainWindow(new DashboardEngine(), desktopServices: desktop.Services with { Log = log }, profileLibrary: new(Path.Combine(folder, "Profiles")));
            await main.InitializeForLaunchAsync();
            var model = (DashboardViewModel)main.DataContext;
            model.ProfileName = "private profile name";
            model.Editor.LowerDeadzone = "secret draft text";
            desktop.Store.Fail = true;
            await main.SavePreferencesAsync();
            var path = Path.Combine(folder, "diagnostics.zip");
            await main.ExportDiagnosticsAsync(path);
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("logs/app.jsonl")!.Open());
            var text = reader.ReadToEnd();
            Assert.Contains("Test settings disk failure", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private profile name", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret draft text", text, StringComparison.Ordinal);
            using var report = JsonDocument.Parse(new StreamReader(zip.GetEntry("report.json")!.Open()).ReadToEnd());
            Assert.Equal("en", report.RootElement.GetProperty("status").GetProperty("language").GetString());
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    });


}
