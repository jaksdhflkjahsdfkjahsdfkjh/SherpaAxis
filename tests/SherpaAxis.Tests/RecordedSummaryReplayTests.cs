using SherpaAxis.Core;

namespace SherpaAxis.Tests;

public sealed class RecordedSummaryReplayTests
{
    // Independently measured phase/minute means from released and held pedal captures; these are not raw USB reports.
    [Fact]
    public void SustainedMeasuredLightInputIsNeverAbsorbedIntoTheManualBaseline()
    {
        const double released = 3_328_590.9864684264;
        const double held = 3_507_003.8163832;
        const double releasedAfter = 3_330_199.777777778;
        var processor = new AxisProcessor(new AxisCalibration(3_474_616, 4_751_769), maximumBaselineCorrection: 150_000);
        processor.ConfirmReleasedBaseline(released);
        var correction = processor.BaselineCorrection;
        Assert.Equal(0, processor.Process(released).Output);
        for (var index = 0; index < 30_000; index++)
        {
            var result = processor.Process(held);
            Assert.InRange(result.Output, .139, .141);
            Assert.Equal(correction, result.BaselineCorrection);
            Assert.Equal(held, result.Input);
        }

        Assert.InRange(processor.Process(releasedAfter).Output, .001, .002);
        Assert.Equal(correction, processor.BaselineCorrection);
    }

    [Fact]
    public void WarmTraceMovementRemainsVisibleWithoutAutomaticZeroTracking()
    {
        const double firstMinute = 3_330_194.770777078;
        const double peakMinute = 3_331_023.3021;
        const double lastMinute = 3_329_954.102366667;
        var processor = new AxisProcessor(new AxisCalibration(3_474_616, 4_751_769), maximumBaselineCorrection: 150_000);
        processor.ConfirmReleasedBaseline(firstMinute);
        var correction = processor.BaselineCorrection;
        foreach (var sample in new[] { firstMinute, peakMinute, lastMinute })
        {
            var result = processor.Process(sample);
            Assert.Equal(correction, result.BaselineCorrection);
            Assert.Equal(sample, result.Input);
            Assert.Equal(sample - firstMinute, result.CorrectedInput - 3_474_616, 8);
            Assert.InRange(result.Output, 0, 1);
        }

        Assert.True(processor.Process(peakMinute).Output > 0);
        Assert.Equal(0, processor.Process(lastMinute).Output);
        Assert.True(processor.Process(lastMinute).CorrectedInput < 3_474_616);
    }
}
