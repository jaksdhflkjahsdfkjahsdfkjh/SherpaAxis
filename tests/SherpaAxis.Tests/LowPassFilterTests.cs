using SherpaAxis.Core;

namespace SherpaAxis.Tests;

public sealed class LowPassFilterTests
{
    [Fact]
    public void DisabledFilterPreservesExistingResultsExactly()
    {
        var processor = new AxisProcessor(new AxisCalibration(100, 200), maximumBaselineCorrection: 10);
        processor.ConfirmReleasedBaseline(105);
        foreach (var input in new[] { -10d, 100, 105, 130, 200, 1000 })
        {
            var result = processor.Process(input, 0);
            Assert.Equal(processor.Process(input), result);
            Assert.Equal(result.CorrectedInput, result.FilteredInput);
            Assert.Equal(result.CorrectedNormalized, result.FilteredNormalized);
            Assert.False(result.FilterReset);
        }
    }

    [Fact]
    public void EnabledFilterRequiresTimestampsAndSeedsFromTheFirstMeasurement()
    {
        var processor = Processor();
        Assert.Throws<InvalidOperationException>(() => processor.Process(.5));
        var first = processor.Process(.5, 10);
        Assert.Equal(.5, first.FilteredInput);
        Assert.Equal(.5, first.Output);
        Assert.True(first.FilterReset);
    }

    [Fact]
    public void ElapsedTimeDeterminesResponseAcrossDifferentReportSchedules()
    {
        var regular = Processor();
        var irregular = Processor();
        var sparse = Processor();
        regular.Process(0, 0);
        irregular.Process(0, 0);
        sparse.Process(0, 0);
        for (var index = 1; index <= 20; index++)
        {
            regular.Process(1, index * .001);
        }

        foreach (var seconds in new[] { .001, .004, .009, .02 })
        {
            irregular.Process(1, seconds);
        }

        var expected = 1 - Math.Exp(-1);
        Assert.Equal(expected, regular.Process(1, .02).FilteredInput, 12);
        Assert.Equal(expected, irregular.Process(1, .02).FilteredInput, 12);
        Assert.Equal(expected, sparse.Process(1, .02).FilteredInput, 12);
    }

    [Fact]
    public void EqualTimestampsRetainTheNewMeasurementWithoutInventingElapsedTime()
    {
        var processor = Processor();
        processor.Process(0, 0);
        var duplicate = processor.Process(1, 0);
        Assert.Equal(1, duplicate.Input);
        Assert.Equal(1, duplicate.CorrectedNormalized);
        Assert.Equal(0, duplicate.FilteredInput);
        Assert.False(duplicate.FilterReset);
        Assert.True(processor.Process(1, .002).Output > 0);
    }

    [Fact]
    public void AGapAtTheConfiguredThresholdSeedsInsteadOfReplayingHistory()
    {
        var processor = Processor();
        processor.Process(0, 0);
        var result = processor.Process(.8, .1);
        Assert.True(result.FilterReset);
        Assert.Equal(.8, result.Output);
        Assert.False(processor.Process(.8, .102).FilterReset);
    }

    [Fact]
    public void ALongGapRetainsTheExplicitBaselineButResetsFilterHistory()
    {
        var processor = new AxisProcessor(new AxisCalibration(0, 100), maximumBaselineCorrection: 10,
            filterSettings: new(.02));
        processor.ConfirmReleasedBaseline(5);
        processor.Process(5, 0);
        processor.Process(95, .002);
        var result = processor.Process(55, 1);
        Assert.True(result.FilterReset);
        Assert.Equal(5, result.BaselineCorrection);
        Assert.Equal(50, result.FilteredInput);
        Assert.Equal(.5, result.Output);
    }

    [Fact]
    public void BaselineConfirmationAndResetDiscardFilterHistory()
    {
        var processor = new AxisProcessor(new AxisCalibration(0, 100), maximumBaselineCorrection: 10,
            filterSettings: new(.02));
        processor.Process(0, 0);
        processor.Process(100, .002);
        processor.ConfirmReleasedBaseline(5);
        Assert.True(processor.Process(55, .004).FilterReset);
        Assert.Equal(.5, processor.Process(55, .004).Output);
        processor.ResetBaseline();
        var result = processor.Process(30, .006);
        Assert.Equal(0, result.BaselineCorrection);
        Assert.True(result.FilterReset);
        Assert.Equal(.3, result.Output);
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.ConfirmReleasedBaseline(20));
        Assert.False(processor.Process(100, .008).FilterReset);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void InvalidTimestampsDoNotAdvanceFilterState(double seconds)
    {
        var processor = Processor();
        processor.Process(0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.Process(1, seconds));
        Assert.Equal(1 - Math.Exp(-1), processor.Process(1, .02).Output, 12);
    }

    [Fact]
    public void ReversedTimeAndInvalidInputLeaveStateUntouched()
    {
        var processor = Processor();
        processor.Process(0, .01);
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.Process(1, .005));
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.Process(double.NaN, 1));
        Assert.Equal(1 - Math.Exp(-1), processor.Process(1, .03).Output, 12);
        processor.ResetBaseline();
        Assert.True(processor.Process(.3, 0).FilterReset);
    }

    [Fact]
    public void FilteringPrecedesClippingSoNegativeNoiseIsNotRectifiedFirst()
    {
        var processor = new AxisProcessor(new AxisCalibration(0, 100), filterSettings: new(.01));
        processor.Process(-1, 0);
        var result = processor.Process(1, .01);
        var expected = 1 - 2 * Math.Exp(-1);
        Assert.Equal(expected, result.FilteredInput, 12);
        Assert.Equal(.01, result.CorrectedNormalized);
        Assert.Equal(expected / 100, result.FilteredNormalized, 12);
        Assert.Equal(result.FilteredNormalized, result.Output);
    }

    [Fact]
    public void FilteringPrecedesNonlinearDeadzoneAndCurveProcessing()
    {
        var processor = new AxisProcessor(new AxisCalibration(0, 100), new AxisDeadzone(.2, .2),
            ResponseCurve.Power(2), filterSettings: new(.01));
        processor.Process(0, 0);
        var result = processor.Process(100, .01);
        var normalized = 1 - Math.Exp(-1);
        var afterDeadzone = (normalized - .2) / .6;
        Assert.Equal(1, result.CorrectedNormalized);
        Assert.Equal(normalized, result.FilteredNormalized, 12);
        Assert.Equal(afterDeadzone, result.AfterDeadzone, 12);
        Assert.Equal(afterDeadzone * afterDeadzone, result.Output, 12);
    }

    [Fact]
    public void SustainedLightInputRemainsVisibleAndNeverAdjustsTheBaseline()
    {
        var processor = new AxisProcessor(new AxisCalibration(100, 200), maximumBaselineCorrection: 10,
            filterSettings: new(.02));
        processor.ConfirmReleasedBaseline(105);
        processor.Process(105, 0);
        for (var index = 1; index <= 30_000; index++)
        {
            var result = processor.Process(115, index * .002);
            Assert.Equal(5, result.BaselineCorrection);
            Assert.Equal(115, result.Input);
            Assert.InRange(result.Output, 0, .1);
        }

        Assert.Equal(.1, processor.Process(115, 60).Output, 12);
    }

    [Fact]
    public void ExtremeFiniteValuesRemainFiniteAndWithinTheInputEnvelope()
    {
        var processor = new AxisProcessor(new AxisCalibration(-double.MaxValue / 2, double.MaxValue / 2),
            filterSettings: new(.02));
        processor.Process(double.MaxValue, 0);
        var result = processor.Process(-double.MaxValue, .002);
        Assert.True(double.IsFinite(result.FilteredInput));
        Assert.InRange(result.FilteredInput, -double.MaxValue, double.MaxValue);
        Assert.InRange(result.Output, 0, 1);
    }

    [Fact]
    public void AVerySmallPositiveWeightDoesNotRoundToZero()
    {
        var processor = new AxisProcessor(new AxisCalibration(0, 1), filterSettings: new(double.MaxValue));
        processor.Process(0, 0);
        Assert.True(processor.Process(1, .001).FilteredInput > 0);
    }

    [Theory]
    [InlineData(0, .1)]
    [InlineData(-1, .1)]
    [InlineData(double.NaN, .1)]
    [InlineData(double.PositiveInfinity, .1)]
    [InlineData(.02, 0)]
    [InlineData(.02, -1)]
    [InlineData(.02, double.NaN)]
    [InlineData(.02, double.PositiveInfinity)]
    public void RejectsInvalidFilterIntervals(double timeConstant, double resetAfter) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LowPassFilterSettings(timeConstant, resetAfter));

    private static AxisProcessor Processor() => new(new AxisCalibration(0, 1), filterSettings: new(.02));
}
