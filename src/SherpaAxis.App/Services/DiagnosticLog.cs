using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SherpaAxis.App.Services;

/// <summary>Bounded, best-effort support logging. Never receives pedal reports or profile documents.</summary>
public sealed class DiagnosticLog(string directory, int maximumBytes = 256 * 1024)
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    private readonly object sync = new();
    private readonly Dictionary<string, long> recent = [];
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public static DiagnosticLog Default { get; } = new(AppDataPaths.Logs);
    public string? Failure { get; private set; }

    public void Write(string category, string message, Exception? exception = null)
    {
        try
        {
            var raw = message + (exception is null ? "" : "\n" + exception);
            if (raw.Length > 16000) { raw = raw[..16000]; }
            var detail = Sanitize(raw);
            var limit = Math.Min(12000, maximumBytes / 8);
            if (detail.Length > limit) { detail = detail[..limit]; }
            if (category.Length > 64) { category = category[..64]; }
            lock (sync)
            {
                var key = category + detail;
                var now = Environment.TickCount64;
                if (recent.TryGetValue(key, out var last) && now - last < 60000) { return; }
                if (recent.Count >= 64) { recent.Clear(); }
                recent[key] = now;
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "app.jsonl");
                var line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, category, detail }) + "\n";
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > maximumBytes)
                {
                    var previous = Path.Combine(DirectoryPath, "app.1.jsonl");
                    if (File.Exists(previous)) { File.Move(previous, Path.Combine(DirectoryPath, "app.2.jsonl"), true); }
                    File.Move(path, previous, true);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
                Failure = null;
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Failure = "Local diagnostic log could not be written."; }
    }

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
                    logging = Failure ?? "Available",
                    status = status.ToDictionary(pair => pair.Key, pair => Sanitize(pair.Value)),
                    privacy = "No profile contents, recordings, firmware images, calibration backups, controller serials or user paths are intentionally included. Review before sharing."
                };
                using (var writer = new StreamWriter(archive.CreateEntry("report.json").Open()))
                { writer.Write(JsonSerializer.Serialize(info, ReportOptions)); }
                lock (sync)
                {
                    foreach (var name in new[] { "app.jsonl", "app.1.jsonl", "app.2.jsonl" })
                    {
                        var source = Path.Combine(DirectoryPath, name);
                        if (!File.Exists(source)) { continue; }
                        // Bound reads as well: another process or an old version may have left a large file.
                        using var reader = new StreamReader(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                        var buffer = new char[maximumBytes];
                        var count = reader.ReadBlock(buffer, 0, buffer.Length);
                        using var writer = new StreamWriter(archive.CreateEntry("logs/" + name).Open());
                        writer.Write(Sanitize(new string(buffer, 0, count)));
                    }
                }
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    internal static string Sanitize(string text)
    {
        // Redact before writing to disk, including paths embedded in exception messages and stack traces.
        text = Regex.Replace(text, @"(?i)PE0[0-9A-F]{6,}", "[controller]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"(?i)(?:[A-Z]:[\\/]|\\\\)[^\r\n\""<>|]*", "[path]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return text;
    }
}
