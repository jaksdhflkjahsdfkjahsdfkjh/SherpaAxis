using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SherpaAxis.App.Services;

public sealed class DiagnosticReportExporter(DiagnosticLog log)
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    public void Export(string path, IReadOnlyDictionary<string, string> status)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var info = new
                {
                    schema = 1,
                    utc = DateTimeOffset.UtcNow,
                    appVersion = typeof(DiagnosticLog).Assembly.GetName().Version?.ToString(),
                    os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    runtime = RuntimeInformation.FrameworkDescription,
                    logging = log.Failure ?? "Available",
                    status = status.ToDictionary(pair => pair.Key, pair => DiagnosticLog.Sanitize(pair.Value)),
                    privacy = "No profile contents, recordings, firmware images, calibration backups, controller serials or user paths are intentionally included. Review before sharing."
                };
                using (var writer = new StreamWriter(archive.CreateEntry("report.json").Open()))
                { writer.Write(JsonSerializer.Serialize(info, ReportOptions)); }
                foreach (var (name, content) in log.ReadRecent())
                {
                    using var writer = new StreamWriter(archive.CreateEntry("logs/" + name).Open());
                    writer.Write(DiagnosticLog.Sanitize(content));
                }
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

}
