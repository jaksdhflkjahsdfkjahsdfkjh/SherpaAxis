using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.Tests;

public sealed class AxisSessionTests
{
    [Fact]
    public void RequestsNeedAnEstablishedSessionAndDoNotGrantReleasePermission()
    {
        var session = Session();
        Assert.Throws<InvalidOperationException>(() => session.ConfirmReleasedBaseline(95));
        Assert.Equal(AxisSessionResetReason.Started, session.Process(95, 0, 1).ResetReason);
        session.ConfirmReleasedBaseline(95);
        for (var index = 1; index <= 100; index++)
        {
            var result = session.Process(105, index * .01, 1);
            Assert.Equal(0, result.Processing.BaselineCorrection);
            Assert.Equal(-5, result.TargetCorrection);
            Assert.True(result.CorrectionSuspended);
            Assert.Equal(.05, result.Processing.Output, 12);
        }
    }

    [Fact]
    public void CountRateUsesElapsedTimeAndBothEnableAndDisableRamp()
    {
        var session = Session();
        session.Process(95, 0, 1);
        session.ConfirmReleasedBaseline(95);
        Assert.Equal(-.1, session.Process(95, .01, 1, true).Processing.BaselineCorrection, 12);
        Assert.Equal(-.4, session.Process(95, .04, 1, true).Processing.BaselineCorrection, 12);
        Assert.Equal(-.4, session.Process(98, .04, 1, true).Processing.BaselineCorrection, 12);
        session.DisableCompensation();
        Assert.Equal(-.2, session.Process(95, .06, 1, true).Processing.BaselineCorrection, 12);
        Assert.Equal(0, session.Process(95, .09, 1, true).Processing.BaselineCorrection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputEffectIsBoundedThroughAggressiveCurvesDeadzonesAndInversion(bool inverted)
    {
        var session = new AxisSession(new(0, 100, inverted), new(1000, .02, 1),
            new(.1, .2), ResponseCurve.Power(.15), 30);
        var input = inverted ? 89.9 : 10.1;
        session.Process(input, 0, 1);
        session.ConfirmReleasedBaseline(inverted ? 120 : -20);
        var previous = 0d;
        var sawOutputLimit = false;
        for (var index = 1; index <= 200; index++)
        {
            if (index == 101)
            {
                session.DisableCompensation();
            }

            var result = session.Process(input, index * .01, 1, true);
            var dt = index * .01 - (index - 1) * .01;
            Assert.True(Math.Abs(result.CorrectionOutputDelta) <= .02 * dt);
            Assert.InRange(Math.Abs(result.Processing.BaselineCorrection - previous), 0, 1000 * dt + 1e-12);
            sawOutputLimit |= Math.Abs(result.Processing.BaselineCorrection - previous) < .1;
            previous = result.Processing.BaselineCorrection;
        }

        Assert.True(sawOutputLimit);
    }

    [Fact]
    public void InputMovementIsNotSubjectToTheCorrectionSlewLimit()
    {
        var session = Session();
        session.Process(100, 0, 1);
        session.ConfirmReleasedBaseline(105);
        var released = session.Process(105, .01, 1, true);
        var pressed = session.Process(200, .012, 1);
        Assert.True(pressed.Processing.Output - released.Processing.Output > .9);
        Assert.Equal(released.Processing.BaselineCorrection, pressed.Processing.BaselineCorrection);
        Assert.Equal(0, pressed.CorrectionOutputDelta);
        Assert.True(pressed.CorrectionSuspended);
        session.DisableCompensation();
        Assert.Equal(pressed.Processing.BaselineCorrection, session.Process(120, .014, 1).Processing.BaselineCorrection);
        Assert.Equal(0, session.Process(100, .034, 1, true).Processing.BaselineCorrection);
    }

    [Theory]
    [InlineData(1, .2, AxisSessionResetReason.SamplingGap)]
    [InlineData(2, .02, AxisSessionResetReason.ConnectionChanged)]
    public void DiscontinuitiesClearCorrectionRequestsAndFilterHistory(long connection, double seconds, AxisSessionResetReason reason)
    {
        var session = Session(new(.02));
        session.Process(95, 0, 1);
        session.ConfirmReleasedBaseline(95);
        session.Process(195, .01, 1, true);
        var reset = session.Process(150, seconds, connection, true);
        Assert.Equal(reason, reset.ResetReason);
        Assert.Equal(0, reset.Processing.BaselineCorrection);
        Assert.Equal(0, reset.TargetCorrection);
        Assert.True(reset.Processing.FilterReset);
        Assert.Equal(150, reset.Processing.FilteredInput);
        Assert.Equal(.5, reset.Processing.Output);
        Assert.Equal(0, reset.CorrectionOutputDelta);
    }

    [Fact]
    public void GapThresholdIsInclusiveAndExplicitResetAllowsANewClockEpoch()
    {
        var session = Session();
        session.Process(95, 0, 3);
        session.ConfirmReleasedBaseline(95);
        Assert.Equal(AxisSessionResetReason.SamplingGap, session.Process(95, .1, 3).ResetReason);
        session.Reset();
        Assert.Throws<InvalidOperationException>(() => session.ConfirmReleasedBaseline(95));
        Assert.Equal(AxisSessionResetReason.Started, session.Process(120, 0, 1).ResetReason);
        Assert.Equal(0, session.TargetCorrection);
    }

    [Fact]
    public void AShorterFilterGapCanReseedInputWithoutInvalidatingTheSession()
    {
        var session = Session(new(.01, .02));
        session.Process(95, 0, 1);
        session.ConfirmReleasedBaseline(95);
        var before = session.Process(105, .01, 1, true);
        var after = session.Process(150, .04, 1);
        Assert.Equal(AxisSessionResetReason.None, after.ResetReason);
        Assert.True(after.Processing.FilterReset);
        Assert.Equal(before.Processing.BaselineCorrection, after.Processing.BaselineCorrection);
        Assert.Equal(-5, after.TargetCorrection);
        Assert.Equal(150 - after.Processing.BaselineCorrection, after.Processing.FilteredInput);
    }

    [Fact]
    public void InvalidReportsAndBaselineRequestsLeaveTheSessionUnchanged()
    {
        var session = Session(new(.02));
        var reference = Session(new(.02));
        foreach (var processor in new[] { session, reference })
        {
            processor.Process(95, 0, 2);
            processor.ConfirmReleasedBaseline(95);
            processor.Process(110, .01, 2, true);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => session.Process(double.NaN, 1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Process(100, double.PositiveInfinity, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Process(100, .009, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Process(100, .02, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Process(100, .02, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ConfirmReleasedBaseline(89));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ConfirmReleasedBaseline(double.NaN));
        Assert.Equal(reference.Process(125, .03, 2, true), session.Process(125, .03, 2, true));
    }

    [Fact]
    public void CorrectionChangesDoNotResetOrPolluteTheRawInputFilter()
    {
        var session = Session(new(.02));
        session.Process(100, 0, 1);
        session.ConfirmReleasedBaseline(105);
        var result = session.Process(150, .01, 1, true);
        var filteredRaw = 100 + 50 * (1 - Math.Exp(-.5));
        Assert.False(result.Processing.FilterReset);
        Assert.Equal(filteredRaw - .1, result.Processing.FilteredInput, 12);
        Assert.Equal(149.9, result.Processing.CorrectedInput, 12);
        Assert.Equal(150, result.Processing.Input);
        session.DisableCompensation();
        Assert.False(session.Process(150, .02, 1, true).Processing.FilterReset);
    }

    [Fact]
    public void LoadingOrSwitchingProfilesCreatesFreshSessionState()
    {
        var profile = new AxisProfile(new(100, 200), maximumBaselineCorrection: 10, filter: new(.02));
        var first = profile.CreateSession(new(10, 1));
        first.Process(95, 0, 1);
        first.ConfirmReleasedBaseline(95);
        first.Process(195, .01, 1, true);
        var restored = ProfileJson.Deserialize(ProfileJson.Serialize(new("Test", new Dictionary<PedalAxis, AxisProfile>
        {
            [PedalAxis.Brake] = profile
        })));
        var second = restored.Axes[PedalAxis.Brake].CreateSession(new(10, 1));
        var result = second.Process(150, 100, 1);
        Assert.Equal(AxisSessionResetReason.Started, result.ResetReason);
        Assert.Equal(0, result.TargetCorrection);
        Assert.Equal(0, result.Processing.BaselineCorrection);
        Assert.Equal(.5, result.Processing.Output);
        Assert.True(result.Processing.FilterReset);
    }

    [Fact]
    public void ReplayedLightHoldCannotBeAbsorbedByAPendingCorrection()
    {
        const double released = 3328590.9864684264;
        const double held = 3507003.8163832;
        var session = new AxisSession(new(3474616, 4751769), new(50000, .1),
            maximumBaselineCorrection: 150000, filterSettings: new(.01));
        session.Process(released, 0, 1);
        session.ConfirmReleasedBaseline(released);
        for (var index = 1; index <= 1000; index++)
        {
            session.Process(released, index * .002, 1, true);
        }

        var correction = session.BaselineCorrection;
        Assert.True(correction > session.TargetCorrection);
        AxisSessionResult result = default;
        for (var index = 1001; index <= 31000; index++)
        {
            result = session.Process(held, index * .002, 1);
            Assert.Equal(correction, result.Processing.BaselineCorrection);
            Assert.Equal(held, result.Processing.Input);
            Assert.Equal(0, result.CorrectionOutputDelta);
        }

        Assert.True(result.CorrectionSuspended);
        Assert.Equal((held - correction - 3474616) / 1277153, result.Processing.Output, 12);
        Assert.True(result.Processing.Output > .09);
    }

    [Fact]
    public void ReplacingAPendingTargetDoesNotAccumulateCorrections()
    {
        var session = Session();
        session.Process(95, 0, 1);
        session.ConfirmReleasedBaseline(95);
        session.Process(95, .01, 1, true);
        session.ConfirmReleasedBaseline(108);
        var result = session.Process(108, .02, 1, true);
        Assert.Equal(8, result.TargetCorrection);
        Assert.Equal(0, result.Processing.BaselineCorrection, 12);
        Assert.Equal(8, session.TargetCorrection);
        var disabled = new AxisSession(new(100, 200), new(10, 1));
        disabled.Process(100, 0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => disabled.ConfirmReleasedBaseline(101));
    }

    [Fact]
    public void IrregularNoisyReplayBoundsCorrectionEffectIndependentlyOfPedalMovement()
    {
        var session = new AxisSession(new(100, 200), new(12, .03), new(.05, .1),
            ResponseCurve.PiecewiseLinear([new(0, 0), new(.01, .8), new(.9, .9), new(1, 1)]), 20, new(.01));
        var calibration = new AxisCalibration(100, 200);
        var deadzone = new AxisDeadzone(.05, .1);
        var curve = ResponseCurve.PiecewiseLinear([new(0, 0), new(.01, .8), new(.9, .9), new(1, 1)]);
        session.Process(100, 0, 1);
        session.ConfirmReleasedBaseline(110);
        var seconds = 0d;
        var previousCorrection = 0d;
        for (var index = 1; index <= 2000; index++)
        {
            var previousSeconds = seconds;
            seconds += index % 4 == 0 ? 0 : index % 3 == 0 ? .003 : .002;
            if (index == 1000)
            {
                session.DisableCompensation();
            }

            var input = 100 + index % 37;
            var released = index % 5 != 0;
            var result = session.Process(input, seconds, 1, released);
            var correction = result.Processing.BaselineCorrection;
            var filteredRaw = result.Processing.FilteredInput + correction;
            var withoutStep = curve.Apply(deadzone.Apply(calibration.Normalize(filteredRaw - previousCorrection)));
            Assert.Equal(result.Processing.Output - withoutStep, result.CorrectionOutputDelta, 12);
            Assert.True(Math.Abs(result.CorrectionOutputDelta) <= .03 * (seconds - previousSeconds));
            Assert.InRange(Math.Abs(correction - previousCorrection), 0, 12 * (seconds - previousSeconds) + 1e-12);
            if (!released || seconds == previousSeconds)
            {
                Assert.Equal(previousCorrection, correction);
            }

            Assert.Equal(input, result.Processing.Input);
            Assert.InRange(result.Processing.Output, 0, 1);
            previousCorrection = correction;
        }
    }

    [Fact]
    public void OverflowRejectionDoesNotResetAnEstablishedSession()
    {
        var session = new AxisSession(new(0, double.MaxValue), new(1, 1), maximumBaselineCorrection: double.MaxValue / 2);
        session.Process(0, 0, 1);
        session.ConfirmReleasedBaseline(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Process(double.MaxValue, 2, 2));
        var next = session.Process(0, .01, 1, true);
        Assert.Equal(AxisSessionResetReason.None, next.ResetReason);
        Assert.Equal(1, next.TargetCorrection);
        Assert.Equal(.01, next.Processing.BaselineCorrection);
    }

    [Theory]
    [InlineData(0, 1, .1)]
    [InlineData(-1, 1, .1)]
    [InlineData(double.NaN, 1, .1)]
    [InlineData(1, double.PositiveInfinity, .1)]
    [InlineData(1, 0, .1)]
    [InlineData(1, 1, 0)]
    public void InvalidTransitionSettingsAreRejected(double counts, double output, double gap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisSessionSettings(counts, output, gap));
    }

    private static AxisSession Session(LowPassFilterSettings? filter = null) => new(new(100, 200), new(10, 1),
        maximumBaselineCorrection: 10, filterSettings: filter);
}
