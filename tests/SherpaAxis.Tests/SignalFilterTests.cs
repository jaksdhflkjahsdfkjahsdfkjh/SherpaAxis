using System.Text;
using System.Text.Json.Nodes;
using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.Tests;

public sealed class SignalFilterTests
{
    [Theory]
    [InlineData(SignalFilterKind.Exponential)]
    [InlineData(SignalFilterKind.TwoStageExponential)]
    [InlineData(SignalFilterKind.MovingAverage)]
    [InlineData(SignalFilterKind.Median)]
    [InlineData(SignalFilterKind.WeightedMovingAverage)]
    [InlineData(SignalFilterKind.MedianExponential)]
    public void FiltersAreBoundedResetOnGapsAndDoNotAdvanceAtDuplicateTimes(SignalFilterKind kind)
    {
        var processor = new AxisProcessor(new(0, 100), filterSettings: new(.01, .1, kind, 5));
        Assert.Equal(20, processor.Process(20, 0).FilteredInput);
        Assert.Equal(20, processor.Process(100, 0).FilteredInput);
        for (var i = 1; i < 40; i++)
        {
            var result = processor.Process(i % 2 == 0 ? 20 : 100, i * .002);
            Assert.InRange(result.FilteredInput, 20, 100);
            Assert.InRange(result.Output, .2, 1);
        }
        var reset = processor.Process(90, 1);
        Assert.True(reset.FilterReset);
        Assert.Equal(90, reset.FilteredInput);
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.Process(10, .5));
        Assert.Equal(90, processor.Process(0, 1).FilteredInput);
    }

    [Fact]
    public void WeightedAverageFavoursRecentInputAndMedianExponentialRejectsAnIsolatedSpike()
    {
        var weighted = Processor(SignalFilterKind.WeightedMovingAverage);
        weighted.Process(0, 0);
        Assert.Equal(20, weighted.Process(30, .002).FilteredInput, 12);
        Assert.Equal(40, weighted.Process(60, .004).FilteredInput, 12);
        Assert.Equal(75, weighted.Process(100, .006).FilteredInput, 12);
        var robust = Processor(SignalFilterKind.MedianExponential);
        robust.Process(10, 0);
        robust.Process(10, .002);
        Assert.Equal(10, robust.Process(100, .004).FilteredInput, 12);
        Assert.Equal(10, robust.Process(10, .006).FilteredInput, 12);
    }

    [Fact]
    public void MedianRejectsSingleSpikeAndAverageIncludesOnlyTheLastWindow()
    {
        var median = Processor(SignalFilterKind.Median);
        var average = Processor(SignalFilterKind.MovingAverage);
        double[] input = [10, 10, 10, 100, 10, 10, 10];
        for (var i = 0; i < input.Length; i++)
        {
            Assert.Equal(10, median.Process(input[i], i * .002).FilteredInput);
            var expected = input.Skip(Math.Max(0, i - 2)).Take(Math.Min(3, i + 1)).Average();
            Assert.Equal(expected, average.Process(input[i], i * .002).FilteredInput, 10);
        }
    }

    [Fact]
    public void SecondExponentialStageReducesAStepWithoutOvershoot()
    {
        var first = Processor(SignalFilterKind.Exponential);
        var second = Processor(SignalFilterKind.TwoStageExponential);
        first.Process(0, 0);
        second.Process(0, 0);
        var previous = 0d;
        for (var i = 1; i <= 50; i++)
        {
            var a = first.Process(100, i * .002).FilteredInput;
            var b = second.Process(100, i * .002).FilteredInput;
            Assert.InRange(b, previous, a);
            previous = b;
        }
        Assert.True(previous > 99.9);
    }

    [Theory]
    [InlineData(SignalFilterKind.Exponential)]
    [InlineData(SignalFilterKind.TwoStageExponential)]
    [InlineData(SignalFilterKind.MovingAverage)]
    [InlineData(SignalFilterKind.Median)]
    [InlineData(SignalFilterKind.WeightedMovingAverage)]
    [InlineData(SignalFilterKind.MedianExponential)]
    public void SchemaTwoRoundTripsEveryFilterAndSessionLimits(SignalFilterKind kind)
    {
        var axis = new AxisProfile(new(100, 200), filter: new(.012, .2, kind, 7), session: new(17, .03, .3));
        var original = new PedalProfile("Filter", new Dictionary<PedalAxis, AxisProfile> { [PedalAxis.Brake] = axis });
        var json = ProfileJson.Serialize(original);
        var loaded = ProfileJson.Deserialize(json).Axes[PedalAxis.Brake];
        Assert.Equal(kind, loaded.Filter!.Kind);
        Assert.Equal(7, loaded.Filter.WindowSamples);
        Assert.Equal(17, loaded.Session!.MaximumCorrectionPerSecond);
        Assert.Equal(.03, loaded.Session.MaximumCorrectionOutputPerSecond);
        Assert.Equal(.3, loaded.Session.ResetAfterSeconds);
        Assert.Equal(json, ProfileJson.Serialize(ProfileJson.Deserialize(json)));
    }

    [Fact]
    public void SchemaOneStillMeansOriginalExponentialAndRejectsNewFields()
    {
        const string original = """
            {"schemaVersion":1,"name":"Legacy","axes":{"brake":{"calibration":{"minimum":0,"maximum":100,"inverted":false},
            "deadzone":{"lower":0,"upper":0},"curve":{"kind":"linear"},"filter":{"timeConstantSeconds":0.01,"resetAfterSeconds":0.1},"maximumBaselineCorrection":0}}}
            """;
        var legacy = ProfileJson.Deserialize(Encoding.UTF8.GetBytes(original));
        Assert.Equal(SignalFilterKind.Exponential, legacy.Axes[PedalAxis.Brake].Filter!.Kind);
        Assert.Null(legacy.Axes[PedalAxis.Brake].Session);
        var json = JsonNode.Parse(original)!;
        json["axes"]!["brake"]!["filter"]!["kind"] = "Median";
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(103)]
    public void InvalidWindowsAreRejected(int window) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LowPassFilterSettings(.01, kind: SignalFilterKind.Median, windowSamples: window));

    [Theory]
    [InlineData(SignalFilterKind.Exponential)]
    [InlineData(SignalFilterKind.TwoStageExponential)]
    [InlineData(SignalFilterKind.MovingAverage)]
    [InlineData(SignalFilterKind.Median)]
    [InlineData(SignalFilterKind.WeightedMovingAverage)]
    [InlineData(SignalFilterKind.MedianExponential)]
    public void FiltersPreserveHeldInputAndSessionCorrectionCannotLearnFromIt(SignalFilterKind kind)
    {
        var session = new AxisSession(new(100, 200), new(1000, 1), maximumBaselineCorrection: 20,
            filterSettings: new(.01, .1, kind, 5));
        session.Process(90, 0, 1);
        session.ConfirmReleasedBaseline(90);
        for (var i = 1; i < 300; i++) { session.Process(90, i * .002, 1, true); }
        Assert.Equal(-10, session.BaselineCorrection);
        AxisSessionResult held = default;
        for (var i = 300; i < 600; i++) { held = session.Process(110, i * .002, 1); }
        Assert.Equal(-10, held.Processing.BaselineCorrection);
        Assert.Equal(.2, held.Processing.Output, 9);
        var reconnected = session.Process(110, 1.2, 2);
        Assert.Equal(0, reconnected.Processing.BaselineCorrection);
        Assert.Equal(.1, reconnected.Processing.Output, 12);
        Assert.True(reconnected.Processing.FilterReset);
    }

    private static AxisProcessor Processor(SignalFilterKind kind) => new(new(0, 100), filterSettings: new(.01, .1, kind, 3));
}
