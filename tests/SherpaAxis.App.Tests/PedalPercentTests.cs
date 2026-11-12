using System.Globalization;
using SherpaAxis.App.Controls;

namespace SherpaAxis.App.Tests;

public sealed class PedalPercentTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(.9, "0")]
    [InlineData(6.1, "6")]
    [InlineData(6.5, "6")]
    [InlineData(6.9, "6")]
    [InlineData(99.5, "99")]
    [InlineData(99.9, "99")]
    [InlineData(99.99999999999999, "99")]
    [InlineData(100, "100")]
    public void OverlayLabelsDiscardFractionalPercentages(double percent, string expected)
    {
        var converter = new PedalPercentConverter();
        Assert.Equal(expected, converter.Convert(percent, typeof(string), null!, CultureInfo.GetCultureInfo("uk-UA")));
    }
}
