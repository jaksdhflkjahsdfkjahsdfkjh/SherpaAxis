using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SherpaAxis.App.Controls;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Recording;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;
using SherpaAxis.ReplayChecks;

namespace SherpaAxis.FilterExamples;

internal static class Program
{
    private const string ExpectedCaptureSha256 = "815767840C613D8E8E969F2106D9319FD77E2028147172077948E3314A75F1C6";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Scene[] Scenes = [new("movement", "Press and release", 63.2, 64.8, false), new("detail", "Small variations · zoomed", 20, 20.7, true)];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 2 or > 3) { Console.Error.WriteLine("Usage: SherpaAxis.FilterExamples <recorded-input.csv> <output-directory> [en|uk]"); return 2; }
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Localizer.Instance.Language = args.Length == 3 ? args[2] : "en";
            Generate(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
            return 0;
        }
        catch (Exception failure) when (failure is IOException or ArgumentException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        { Console.Error.WriteLine(failure.Message); return 1; }
    }

    private static void Generate(string input, string destination)
    {
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
        if (sourceHash != ExpectedCaptureSha256)
        { throw new InvalidDataException("These fixed example windows belong to pedals-20261018-124240.csv. Use that capture, or review the scene windows and source description before changing datasets."); }
        var samples = ReadAsync(input).GetAwaiter().GetResult();
        if (samples.Count < 2) { throw new InvalidDataException("Recording has too few samples."); }
        var origin = samples[0].Elapsed.TotalSeconds;
        var duration = samples[^1].Elapsed.TotalSeconds - origin;
        if (duration < Scenes.Max(scene => scene.End)) { throw new InvalidDataException("This generator needs the original 75-second recording; its example windows are fixed."); }
        if (samples.Select(sample => sample.ConnectionNumber).Distinct().Count() != 1) { throw new InvalidDataException("Example windows must use one continuous connection."); }
        var complete = CompletionVerified(input, samples.Count);
        Directory.CreateDirectory(destination);
        var generated = new List<object>();
        foreach (var algorithm in FilteringGuide.Algorithms)
        {
            var profile = new AxisProfile(new(0, PedalInputReport.BrakeMaximum), filter: algorithm.ExampleFilter);
            var session = profile.CreateSession(new(1000, .05));
            var reference = new ReferenceAxis(profile);
            var points = new List<PlotSample>(samples.Count);
            var maximumError = 0d;
            foreach (var sample in samples)
            {
                var time = sample.Elapsed.TotalSeconds - origin;
                var raw = sample.Axes!.Value.Brake;
                var actual = session.Process(raw, time, sample.ConnectionNumber).Processing;
                var expected = reference.Process(raw, time, sample.ConnectionNumber);
                maximumError = Math.Max(maximumError, Math.Abs(actual.FilteredNormalized - expected));
                if (!double.IsFinite(actual.FilteredNormalized) || maximumError > 1e-10) { throw new InvalidDataException($"Reference mismatch for {algorithm.Name}."); }
                points.Add(new(time, actual.Normalized, actual.FilteredNormalized, actual.FilteredNormalized));
            }
            foreach (var scene in Scenes)
            {
                var visible = points.Where(point => point.Time >= scene.Start && point.Time <= scene.End).ToArray();
                if (visible.Length < 100 || visible.Max(point => point.Input) - visible.Min(point => point.Input) < .001)
                { throw new InvalidDataException("Example window has insufficient input variation."); }
                var path = Path.Combine(destination, $"{algorithm.ExampleKey}-{scene.Key}.png");
                Render(path, algorithm, scene, points.Where(point => point.Time >= scene.Start - .1 && point.Time <= scene.End).ToArray());
                generated.Add(new
                {
                    Filter = algorithm.Name,
                    algorithm.ExampleSettings,
                    Settings = algorithm.ExampleFilter,
                    Scene = scene.Title,
                    Start = scene.Start,
                    End = scene.End,
                    scene.Zoom,
                    Reports = visible.Length,
                    RawMinimum = visible.Min(point => point.Input),
                    RawMaximum = visible.Max(point => point.Input),
                    MaximumDifferencePercentagePoints = visible.Max(point => Math.Abs(point.Input - point.Filtered)) * 100,
                    MaximumReferenceError = maximumError,
                    File = Path.GetFileName(path),
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                });
            }
        }
        File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(new
        {
            Source = Path.GetFileName(input),
            Sha256 = sourceHash,
            Reports = samples.Count,
            DurationSeconds = duration,
            SourceOriginElapsedSeconds = origin,
            CompletionVerified = complete,
            ReportsPerSecond = (samples.Count - 1) / duration,
            Comparisons = samples.Count * FilteringGuide.Algorithms.Count,
            Signal = "Firmware-calibrated brake device axis, normalized to 0–100%. Original timestamps; no synthetic noise, resampling, curves, deadzones or correction.",
            Rendering = "SherpaAxis SignalPlot; full-rate offline replay with complete filter warm-up; 640x260 logical pixels rendered at 192 DPI.",
            Images = generated
        }, JsonOptions));
        Console.WriteLine($"Generated {generated.Count} screenshots; {samples.Count * FilteringGuide.Algorithms.Count:N0} independent reference comparisons passed.");
    }

    private static async Task<List<PedalMonitorEvent>> ReadAsync(string input)
    {
        var samples = new List<PedalMonitorEvent>();
        await foreach (var sample in CapturePlayback.ReadAsync(input, 100)) { if (sample.Axes.HasValue) { samples.Add(sample); } }
        return samples;
    }

    private static bool CompletionVerified(string input, int count)
    {
        var path = Path.ChangeExtension(input, ".session.jsonl");
        if (!File.Exists(path)) { return false; }
        using var document = JsonDocument.Parse(File.ReadLines(path).Last());
        var root = document.RootElement;
        if (root.GetProperty("kind").GetString() != "recording-completed" || !root.GetProperty("complete").GetBoolean()
            || root.GetProperty("acceptedSamples").GetInt32() != count || root.GetProperty("writtenSamples").GetInt32() != count)
        { throw new InvalidDataException("Recording completion does not match the input."); }
        return true;
    }

    private static void Render(string path, FilterExplanation algorithm, Scene scene, PlotSample[] points)
    {
        var panel = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(Text($"{algorithm.Name} · {scene.Title}", 15, "#E8EFF8"));
        header.Children.Add(Text(algorithm.ExampleSettings, 11, "#95A9C1"));
        header.Children.Add(Text("Dashed grey: recorded input     Orange: filtered input", 11, "#FFB967"));
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var footer = Text("Real brake capture · original report timing · no curves or baseline adjustment", 10, "#95A9C1");
        footer.Margin = new Thickness(0, 7, 0, 0); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        panel.Children.Add(new SignalPlot
        {
            Series = [new(PedalAxis.Brake, points, scene.Zoom ? "Magnified vertical scale" : "0–100% scale")],
            Pedal = "Brake",
            Mode = "Input vs filtered",
            Zoom = scene.Zoom,
            WindowSeconds = scene.End - scene.Start
        });
        var root = new Border { Background = Brush("#111D2E"), BorderBrush = Brush("#304157"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = panel };
        root.Measure(new Size(640, 260)); root.Arrange(new Rect(0, 0, 640, 260)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 520, 192, 192, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static TextBlock Text(string value, double size, string color) => new()
    { Text = Localizer.Translate(value), FontSize = size, FontFamily = new FontFamily("Segoe UI"), Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private sealed record Scene(string Key, string Title, double Start, double End, bool Zoom);
}
