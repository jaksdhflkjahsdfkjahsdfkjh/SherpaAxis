using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SherpaAxis.App.Services;

/// <summary>Bounded, best-effort support logging. Never receives pedal reports or profile documents.</summary>
public sealed class DiagnosticLog
{
    private readonly object sync = new();
    private readonly Dictionary<string, long> recent = [];
    private readonly int maximumBytes;
    public string DirectoryPath { get; }
    public DiagnosticLog(string directory, int maximumBytes = 256 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maximumBytes is < 1024 or > 4 * 1024 * 1024) { throw new ArgumentOutOfRangeException(nameof(maximumBytes)); }
        DirectoryPath = Path.GetFullPath(directory);
        this.maximumBytes = maximumBytes;
    }
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
                while (Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.Preamble.Length > maximumBytes)
                {
                    detail = detail[..(detail.Length / 2)];
                    category = category[..Math.Min(32, category.Length)];
                    line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, category, detail }) + "\n";
                }
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

    internal IReadOnlyDictionary<string, string> ReadRecent()
    {
        var snapshot = new Dictionary<string, string>();
        lock (sync)
        {
            foreach (var name in new[] { "app.jsonl", "app.1.jsonl", "app.2.jsonl" })
            {
                var path = Path.Combine(DirectoryPath, name);
                if (!File.Exists(path)) { continue; }
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                var buffer = new char[maximumBytes];
                var count = reader.ReadBlock(buffer, 0, buffer.Length);
                snapshot[name] = new string(buffer, 0, count);
            }
        }
        return snapshot;
    }

    internal static string Sanitize(string text)
    {
        // Redact before writing to disk, including paths embedded in exception messages and stack traces.
        text = Regex.Replace(text, @"(?i)PE0[0-9A-F]{6,}", "[controller]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"(?i)(?:[A-Z]:[\\/]|\\\\)[^\r\n\""<>|]*", "[path]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return text;
    }
}
