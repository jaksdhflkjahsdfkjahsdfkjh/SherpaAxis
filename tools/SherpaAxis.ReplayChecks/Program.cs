using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.ReplayChecks;

internal static class Program
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: SherpaAxis.ReplayChecks <input.csv> <new-result-directory>");
            return 2;
        }
        try
        {
            await RunAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
            return 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task RunAsync(string inputPath, string destination)
    {
        if (Directory.Exists(destination) || File.Exists(destination)) { throw new IOException("Result directory must be new."); }
        var samples = new List<PedalMonitorEvent>();
        await foreach (var item in CapturePlayback.ReadAsync(inputPath, 100))
        {
            if (item.Axes.HasValue) { samples.Add(item); }
        }
        if (samples.Count < 2) { throw new InvalidDataException("At least two recorded samples are required."); }
        var completionVerified = VerifyCompletion(inputPath, samples.Count);
        Directory.CreateDirectory(destination);
        var cases = new List<CheckResult>();
        foreach (var axis in DashboardViewModel.AxisNames)
        {
            foreach (var preset in CurvePresets.Names)
            {
                var model = new DashboardViewModel(new DashboardEngine());
                model.SetCurvePreset(axis, preset);
                cases.Add(Check($"{axis}: {preset}", samples, model.ValidateEditors().Profile));
            }
        }

        var starters = StarterProfiles.Create();
        var library = new ProfileLibrary(Path.Combine(destination, "profile-library"));
        foreach (var profile in starters)
        {
            await library.SaveAsync(profile);
            cases.Add(Check(profile.Name, samples, profile));
        }
        var restored = await library.LoadAsync();
        Require(restored.Count == starters.Count && library.LoadErrors.Count == 0, "Profile library count/validation mismatch.");
        foreach (var original in starters)
        {
            var loaded = restored.Single(p => p.Profile.Name == original.Name).Profile;
            Require(ProfileJson.Serialize(original).SequenceEqual(ProfileJson.Serialize(loaded)), "Profile did not round-trip exactly.");
        }
        Require(await library.SelectedPathAsync() == restored.Single(p => p.Profile.Name == starters[^1].Name).Path, "Last selected profile was not retained.");

        var mixed = new DashboardViewModel(new DashboardEngine());
        foreach (var axis in DashboardViewModel.AxisNames)
        {
            mixed.EditCurve(axis, [new(0, 0), new(.25, .1 + (int)axis * .1), new(.7, .8), new(1, 1)]);
        }
        cases.Add(Check("Independent custom points", samples, mixed.ValidateEditors().Profile));
        foreach (var axis in DashboardViewModel.AxisNames)
        {
            mixed.SelectedAxis = axis;
            mixed.Editor.Minimum = (DashboardEngine.DeviceMaximum(axis) * .1).ToString("R", CultureInfo.InvariantCulture);
            mixed.Editor.Maximum = (DashboardEngine.DeviceMaximum(axis) * .9).ToString("R", CultureInfo.InvariantCulture);
            mixed.Editor.Inverted = axis != PedalAxis.Brake;
            mixed.Editor.LowerDeadzone = "3";
            mixed.Editor.UpperDeadzone = "4";
            mixed.Editor.FilterMilliseconds = "10";
            mixed.Editor.FilterKind = axis == PedalAxis.Throttle ? "TwoStageExponential" : axis == PedalAxis.Brake ? "Median" : "MovingAverage";
        }
        var mixedProfile = mixed.ValidateEditors().Profile;
        cases.Add(Check("Custom + calibration + inversion + deadzones + mixed filters", samples, mixedProfile));
        cases.Add(Check("Synthetic duplicate timestamps", samples, mixedProfile, "duplicates"));
        cases.Add(Check("Synthetic sampling gap", samples, mixedProfile, "gap"));
        cases.Add(Check("Synthetic reconnect", samples, mixedProfile, "reconnect"));
        cases.Add(Check("Switch profiles midway", samples, starters[1], "switch", starters[2]));
        cases.Add(Check("Clear throttle midway", samples, mixedProfile, "clear-Throttle"));
        cases.Add(Check("Clear brake midway", samples, mixedProfile, "clear-Brake"));
        cases.Add(Check("Clear clutch midway", samples, mixedProfile, "clear-Clutch"));

        var coverage = DashboardViewModel.AxisNames.Select(axis =>
        {
            var values = samples.Select(s => AxisInput(s.Axes!.Value, axis) / DashboardEngine.DeviceMaximum(axis)).ToArray();
            return new
            {
                Axis = axis.ToString(),
                Minimum = values.Min(),
                Maximum = values.Max(),
                ZeroReports = values.Count(v => v == 0),
                FullReports = values.Count(v => v == 1),
                InteriorReports = values.Count(v => v > 0 && v < 1),
                OccupiedTenths = values.Select(v => Math.Min(9, (int)(v * 10))).Distinct().Order().ToArray()
            };
        }).ToArray();
        var report = new
        {
            Input = Path.GetFileName(inputPath),
            Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(inputPath))).ToLowerInvariant(),
            Reports = samples.Count,
            DurationSeconds = (samples[^1].Elapsed - samples[0].Elapsed).TotalSeconds,
            CompletionVerified = completionVerified,
            ProcessedReports = cases.Sum(c => c.Reports),
            AxisComparisons = 3 * cases.Sum(c => c.Reports),
            Cases = cases,
            Coverage = coverage,
            ProfileLibraryRoundTrip = "passed: four profiles, exact settings, last selection",
            Scope = "Offline software processing only. Original timestamps retained except explicitly synthetic cases. No HID or vJoy access. Presets checked independently per axis; other axes remain linear."
        };
        await File.WriteAllTextAsync(Path.Combine(destination, "summary.json"), JsonSerializer.Serialize(report, ReportOptions));
        Console.WriteLine($"PASS: {cases.Count} scenarios, {cases.Sum(c => c.Reports):N0} processed reports, {3 * cases.Sum(c => c.Reports):N0} axis comparisons. Four saved profiles round-tripped.");
        Console.WriteLine(Path.Combine(destination, "summary.json"));
    }

    private static CheckResult Check(string name, List<PedalMonitorEvent> samples, PedalProfile profile, string mode = "normal", PedalProfile? next = null)
    {
        // Exercise the same profile load/edit/apply path as the desktop, including serialization.
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        model.Load(ProfileJson.Deserialize(ProfileJson.Serialize(profile)));
        var firstTab = model.SelectedProfile!;
        if (next is not null)
        {
            model.Load(ProfileJson.Deserialize(ProfileJson.Serialize(next)));
            model.SwitchProfile(firstTab);
        }
        engine.Begin(new("capture-replay"));
        var checker = new SampleChecker(model.ValidateEditors().Profile);
        engine.AttachRecording(checker);
        var halfway = samples.Count / 2;
        for (var index = 0; index < samples.Count; index++)
        {
            var item = samples[index];
            if (index == halfway && (mode == "switch" || mode.StartsWith("clear-", StringComparison.Ordinal)))
            {
                if (mode == "switch") { model.SwitchProfile(model.ProfileTabs[^1]); }
                else
                {
                    var axis = Enum.Parse<PedalAxis>(mode[6..], true);
                    model.ClearCurve(axis);
                    model.Apply();
                }
                checker.SetProfile(model.ValidateEditors().Profile);
            }
            if (mode == "gap" && index >= halfway) { item = item with { Elapsed = item.Elapsed + TimeSpan.FromSeconds(.2) }; }
            if (mode == "reconnect" && index >= halfway) { item = item with { ConnectionNumber = item.ConnectionNumber + 1 }; }
            engine.Accept(item);
            if (mode == "duplicates") { engine.Accept(item with { Axes = new(0, 0, 0) }); }
        }
        Require(checker.Count == samples.Count * (mode == "duplicates" ? 2 : 1), "Reports were lost during processing.");
        return new(name, checker.Count, checker.MaximumError, true);
    }

    private static bool VerifyCompletion(string input, int count)
    {
        var companion = Path.ChangeExtension(input, ".session.jsonl");
        if (!File.Exists(companion)) { return false; }
        using var final = JsonDocument.Parse(File.ReadLines(companion).Last());
        var root = final.RootElement;
        Require(root.GetProperty("kind").GetString() == "recording-completed" && root.GetProperty("complete").GetBoolean()
            && root.GetProperty("acceptedSamples").GetInt32() == count && root.GetProperty("writtenSamples").GetInt32() == count,
            "Recording completion marker/count does not match its input.");
        return true;
    }

    internal static double AxisInput(PedalAxes input, PedalAxis axis) => axis switch
    {
        PedalAxis.Throttle => input.Throttle,
        PedalAxis.Brake => input.Brake,
        _ => input.Clutch
    };

    internal static void Require(bool condition, string error)
    {
        if (!condition) { throw new InvalidDataException(error); }
    }
}

internal sealed record CheckResult(string Name, long Reports, double MaximumError, bool Passed);

internal sealed class SampleChecker : IDashboardRecording
{
    private Dictionary<PedalAxis, ReferenceAxis> references = [];
    public SampleChecker(PedalProfile profile) => SetProfile(profile);
    public long Count { get; private set; }
    public double MaximumError { get; private set; }
    public void SetProfile(PedalProfile profile) => references = profile.Axes.ToDictionary(p => p.Key, p => new ReferenceAxis(p.Value));
    public void RecordSample(RecordedSample sample)
    {
        Count++;
        foreach (var (axis, reference) in references)
        {
            var result = axis switch { PedalAxis.Throttle => sample.Throttle, PedalAxis.Brake => sample.Brake, _ => sample.Clutch };
            var expected = reference.Process(Program.AxisInput(sample.Input, axis), sample.ElapsedSeconds, sample.Connection);
            var error = Math.Abs(result.Processing.Output - expected);
            MaximumError = Math.Max(MaximumError, error);
            Program.Require(double.IsFinite(result.Processing.Output) && result.Processing.Output is >= 0 and <= 1 && error < 1e-10,
                $"{axis} output mismatch at report {Count}: expected {expected:R}, actual {result.Processing.Output:R}.");
            Program.Require(result.Processing.BaselineCorrection == 0 && result.TargetCorrection == 0, "Unrequested baseline correction.");
        }
    }
    public void RecordEvent(RecordedEvent item) { }
    public void Complete(string reason) { }
}
