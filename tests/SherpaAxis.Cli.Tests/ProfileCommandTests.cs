using System.Globalization;
using System.Text.Json.Nodes;
using SherpaAxis.Profiles;

namespace SherpaAxis.Cli.Tests;

public sealed class ProfileCommandTests
{
    [Fact]
    public async Task CreateAddAxisValidateRenameExportAndReplaceWorkWithoutDeviceAccess()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "profile.json");
        var export = Path.Combine(directory.Path, "export.json");
        await Create(path);
        var original = await File.ReadAllBytesAsync(path);
        await ProfileCommand.RunAsync(["validate", "--input", path], CancellationToken.None);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        await Assert.ThrowsAsync<IOException>(() => Create(path));
        await ProfileCommand.RunAsync(["set-axis", "--input", path, "--json", path,
            "--axis", "throttle", "--minimum", "0", "--maximum", "1000", "--overwrite", "true"], CancellationToken.None);
        var profile = await ProfileStore.LoadAsync(path);
        Assert.Equal(2, profile.Axes.Count);
        Assert.Equal(1000, profile.Axes[PedalAxis.Throttle].Calibration.Maximum);
        Assert.Equal(10, profile.Axes[PedalAxis.Brake].MaximumBaselineCorrection);
        Assert.Equal(.01, profile.Axes[PedalAxis.Brake].Filter!.TimeConstantSeconds);
        await ProfileCommand.RunAsync(["export", "--input", path, "--json", export, "--name", "Copy"], CancellationToken.None);
        Assert.Equal("Copy", (await ProfileStore.LoadAsync(export)).Name);
        Assert.Equal("Test", (await ProfileStore.LoadAsync(path)).Name);
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task ProfileReplayMatchesExplicitSettingsAndRequiresANewSessionBaseline()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "profile.json");
        await Create(path);
        var options = await ReplayOptions.Parse(["--input", "unused", "--csv", "unused-output", "--profile", path,
            "--baseline", "95"]).ResolveAsync(CancellationToken.None);
        var explicitOptions = ReplayOptions.Parse(["--input", "unused", "--csv", "unused-output", "--minimum", "100",
            "--maximum", "200", "--max-correction", "10", "--baseline", "95", "--filter-ms", "10", "--exponent", "2"]);
        const string rows = "elapsed_seconds,connection,throttle_device_axis,brake_device_axis,clutch_device_axis\n0,1,0,95,0\n0.01,1,0,195,0\n0.02,2,0,150,0\n";
        using var input = new StringReader(rows);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var otherInput = new StringReader(rows);
        using var otherOutput = new StringWriter(CultureInfo.InvariantCulture);
        ReplayCommand.Run(input, output, options, CancellationToken.None);
        ReplayCommand.Run(otherInput, otherOutput, explicitOptions, CancellationToken.None);
        Assert.Equal(otherOutput.ToString(), output.ToString());
        var withoutBaseline = await ReplayOptions.Parse(["--input", "unused", "--csv", "unused-output", "--profile", path])
            .ResolveAsync(CancellationToken.None);
        Assert.Null(withoutBaseline.Baseline);
        Assert.Equal(0, withoutBaseline.CreateProcessor().BaselineCorrection);
        Assert.Equal(0, options.CreateProcessor().BaselineCorrection);
    }

    [Fact]
    public async Task MissingAxisAndExcessiveBaselineFailBeforeOutputCreation()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "profile.json");
        var output = Path.Combine(directory.Path, "must-not-exist.csv");
        await Create(path);
        var absent = ReplayOptions.Parse(["--input", "unused", "--csv", output, "--profile", path, "--axis", "throttle"]);
        await Assert.ThrowsAsync<ArgumentException>(() => ReplayCommand.ExecuteAsync(absent));
        var excessive = ReplayOptions.Parse(["--input", "unused", "--csv", output, "--profile", path, "--baseline", "50"]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReplayCommand.ExecuteAsync(excessive));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task FailedImportDoesNotCreateAnExportOrReplayOutput()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "invalid.json");
        var output = Path.Combine(directory.Path, "must-not-exist.json");
        await Create(path);
        var json = JsonNode.Parse(await File.ReadAllBytesAsync(path))!;
        json["schemaVersion"] = 5;
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => ProfileCommand.RunAsync(
            ["export", "--input", path, "--json", output], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReplayCommand.ExecuteAsync(ReplayOptions.Parse(
            ["--input", "unused", "--csv", output, "--profile", path])));
        Assert.False(File.Exists(output));
        Assert.Equal(5, JsonNode.Parse(await File.ReadAllBytesAsync(path))!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void ProfileOverridesAndRuntimeStateInCreationAreRejected()
    {
        foreach (var option in AxisProfileOptions.Names)
        {
            Assert.Throws<ArgumentException>(() => ReplayOptions.Parse(
                ["--input", "unused", "--csv", "unused-output", "--profile", "profile.json", option, "1"]));
        }
    }

    [Fact]
    public async Task CancelledOrInvalidCreationDoesNotLeaveFiles()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "profile.json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProfileCommand.RunAsync(
            ["create", "--json", path, "--name", "Test", "--axis", "brake", "--minimum", "0", "--maximum", "100"], cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => ProfileCommand.RunAsync(
            ["create", "--json", path, "--name", "Test", "--axis", "brake", "--minimum", "0", "--maximum", "100", "--baseline", "1"], CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    private static Task<PedalProfile> Create(string path) => ProfileCommand.RunAsync(
        ["create", "--json", path, "--name", "Test", "--axis", "brake", "--minimum", "100", "--maximum", "200",
            "--max-correction", "10", "--filter-ms", "10", "--exponent", "2"], CancellationToken.None);

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, $"profile-cli-{Guid.NewGuid():N}");

        public TestDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Path))
            {
                File.Delete(file);
            }

            Directory.Delete(Path);
        }
    }
}
