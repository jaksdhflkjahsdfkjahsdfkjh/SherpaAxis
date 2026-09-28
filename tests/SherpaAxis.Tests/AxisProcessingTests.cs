using SherpaAxis.Core;

namespace SherpaAxis.Tests;

public sealed class AxisProcessingTests
{
    [Theory]
    [InlineData(-100, 0, 1)]
    [InlineData(100, 0, 1)]
    [InlineData(125, .25, .75)]
    [InlineData(200, 1, 0)]
    [InlineData(1000, 1, 0)]
    public void NormalizesAndInvertsWithSaturation(double input, double expected, double inverted)
    {
        Assert.Equal(expected, new AxisCalibration(100, 200).Normalize(input), 12);
        Assert.Equal(inverted, new AxisCalibration(100, 200, true).Normalize(input), 12);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(-double.MaxValue, double.MaxValue)]
    public void RejectsInvalidCalibration(double minimum, double maximum) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisCalibration(minimum, maximum));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsInvalidSamplesWithoutChangingBaseline(double input)
    {
        var processor = new AxisProcessor(new AxisCalibration(100, 200), maximumBaselineCorrection: 10);
        processor.ConfirmReleasedBaseline(105);
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.Process(input));
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.ConfirmReleasedBaseline(input));
        Assert.Equal(5, processor.BaselineCorrection);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisDeadzone().Apply(input));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResponseCurve.Linear.Apply(input));
    }

    [Theory]
    [InlineData(.1, 0)]
    [InlineData(.2, 0)]
    [InlineData(.45, .5)]
    [InlineData(.7, 1)]
    [InlineData(.9, 1)]
    public void DeadzonesRescaleTheActiveInputRange(double input, double expected) =>
        Assert.Equal(expected, new AxisDeadzone(.2, .3).Apply(input), 12);

    [Theory]
    [InlineData(-.1, 0)]
    [InlineData(0, 1)]
    [InlineData(.5, .5)]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    public void RejectsDeadzonesWithoutAValidActiveRange(double lower, double upper) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisDeadzone(lower, upper));

    [Fact]
    public void CorrectsBothDirectionsWithoutChangingGainAndResetsExplicitly()
    {
        foreach (var offset in new[] { -10, 10 })
        {
            var processor = new AxisProcessor(new AxisCalibration(100, 200), maximumBaselineCorrection: 10);
            processor.ConfirmReleasedBaseline(100 + offset);
            var zero = processor.Process(100 + offset);
            Assert.Equal(0, zero.Output);
            Assert.Equal(100, zero.CorrectedInput);
            Assert.Equal(.5, processor.Process(150 + offset).Output, 12);
            Assert.Equal(1, processor.Process(200 + offset).Output);
            processor.ResetBaseline();
            Assert.Equal(0, processor.BaselineCorrection);
            Assert.Equal(.5, processor.Process(150).Output);
        }
    }

    [Fact]
    public void UsesTheMaximumAsTheReleasedReferenceWhenInverted()
    {
        var processor = new AxisProcessor(new AxisCalibration(100, 200, true), maximumBaselineCorrection: 10);
        processor.ConfirmReleasedBaseline(195);
        Assert.Equal(-5, processor.BaselineCorrection);
        Assert.Equal(0, processor.Process(195).Output);
        Assert.Equal(.5, processor.Process(145).Output);
        Assert.Equal(1, processor.Process(95).Output);
    }

    [Fact]
    public void NewManualBaselineReplacesInsteadOfAccumulatingAndFailedRequestsPreserveIt()
    {
        var processor = new AxisProcessor(new AxisCalibration(100, 200), maximumBaselineCorrection: 10);
        processor.ConfirmReleasedBaseline(105);
        processor.ConfirmReleasedBaseline(107);
        Assert.Equal(7, processor.BaselineCorrection);
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.ConfirmReleasedBaseline(111));
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.ConfirmReleasedBaseline(89));
        Assert.Equal(7, processor.BaselineCorrection);
        Assert.Equal(0, processor.Process(107).Output);
    }

    [Fact]
    public void DefaultCorrectionLimitDisablesZeroChanges()
    {
        var processor = new AxisProcessor(new AxisCalibration(100, 200));
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.ConfirmReleasedBaseline(101));
        Assert.Equal(0, processor.BaselineCorrection);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsUnboundedOrExcessiveCorrectionLimits(double limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisProcessor(new AxisCalibration(100, 200), maximumBaselineCorrection: limit));

    [Fact]
    public void KeepsOriginalMeasurementsAndAppliesDeadzoneBeforeCurve()
    {
        var processor = new AxisProcessor(new AxisCalibration(0, 100), new AxisDeadzone(.1, .1),
            ResponseCurve.Power(2), maximumBaselineCorrection: 10);
        processor.ConfirmReleasedBaseline(5);
        var result = processor.Process(35);
        Assert.Equal(35, result.Input);
        Assert.Equal(30, result.CorrectedInput);
        Assert.Equal(.35, result.Normalized, 12);
        Assert.Equal(.3, result.CorrectedNormalized, 12);
        Assert.Equal(.25, result.AfterDeadzone, 12);
        Assert.Equal(.0625, result.Output, 12);
        Assert.Equal(5, result.BaselineCorrection);
    }

    [Fact]
    public void RejectsArithmeticOverflowInsteadOfReturningNonfiniteDiagnostics()
    {
        var processor = new AxisProcessor(new AxisCalibration(-double.MaxValue / 2, double.MaxValue / 2),
            maximumBaselineCorrection: double.MaxValue);
        processor.ConfirmReleasedBaseline(-double.MaxValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.Process(double.MaxValue));
    }
}
