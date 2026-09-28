using SherpaAxis.Core;

namespace SherpaAxis.Tests;

public sealed class ResponseCurveTests
{
    [Fact]
    public void LinearProgressiveAndAggressiveCurvesHaveExpectedResponses()
    {
        Assert.Equal(.25, ResponseCurve.Linear.Apply(.25));
        Assert.Equal(.0625, ResponseCurve.Power(2).Apply(.25));
        Assert.Equal(.5, ResponseCurve.Power(.5).Apply(.25));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidPowerExponents(double exponent) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ResponseCurve.Power(exponent));

    [Fact]
    public void InterpolatesControlPointsAndCopiesTheInput()
    {
        CurvePoint[] points = [new(0, 0), new(.5, .2), new(1, 1)];
        var curve = ResponseCurve.PiecewiseLinear(points);
        points[1] = new(.5, .9);
        Assert.Equal(.1, curve.Apply(.25), 12);
        Assert.Equal(.2, curve.Apply(.5), 12);
        Assert.Equal(.6, curve.Apply(.75), 12);
    }

    [Fact]
    public void RejectsAmbiguousOrNonmonotonicControlPoints()
    {
        CurvePoint[][] invalid =
        [
            [], [new(0, 0)], [new(0, .1), new(1, 1)],
            [new(0, 0), new(1, .9)], [new(0, 0), new(0, .5), new(1, 1)],
            [new(0, 0), new(.7, .5), new(.5, .7), new(1, 1)],
            [new(0, 0), new(.3, .8), new(.7, .2), new(1, 1)],
            [new(0, 0), new(.5, 1.1), new(1, 1)],
            [new(0, 0), new(double.NaN, .5), new(1, 1)],
            [new(0, 0), new(.5, double.NaN), new(1, 1)]
        ];
        foreach (var points in invalid)
        {
            Assert.Throws<ArgumentException>(() => ResponseCurve.PiecewiseLinear(points));
        }
    }

    [Fact]
    public void AllBuiltInCurvesRemainBoundedMonotonicAndDeterministic()
    {
        ResponseCurve[] curves = [ResponseCurve.Linear, ResponseCurve.Power(.25), ResponseCurve.Power(4),
            ResponseCurve.PiecewiseLinear([new(0, 0), new(.2, .4), new(.8, .4), new(1, 1)])];
        foreach (var curve in curves)
        {
            Assert.Equal(0, curve.Apply(-double.MaxValue));
            Assert.Equal(1, curve.Apply(double.MaxValue));
            var previous = 0d;
            for (var index = 0; index <= 1000; index++)
            {
                var input = index / 1000d;
                var output = curve.Apply(input);
                Assert.InRange(output, previous, 1);
                Assert.Equal(output, curve.Apply(input));
                previous = output;
            }
        }
    }
}
