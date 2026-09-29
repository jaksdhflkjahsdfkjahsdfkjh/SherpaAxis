using System.Globalization;
using SherpaAxis.Cli;

namespace SherpaAxis.Cli.Tests;

public sealed class ReplayTests
{
    [Fact]
    public void PreservesSelectedInputAndClearsManualCorrectionOnAConnectionChange()
    {
        using var input = Input("0.01,1,0,95,0\n0.02,1,0,105,0\n2.00,2,0,105,0\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var count = ReplayCommand.Run(input, output, Options("--baseline", "95", "--max-correction", "10"), CancellationToken.None);
        var rows = Rows(output);
        Assert.Equal(3, count);
        Assert.Equal("brake_device_axis", rows[0][2]);
        Assert.Equal("95", rows[1][2]);
        Assert.Equal("-5", rows[1][3]);
        Assert.Equal("100", rows[1][4]);
        Assert.Equal("0", rows[1][8]);
        Assert.Equal(.1, double.Parse(rows[2][8], CultureInfo.InvariantCulture), 12);
        Assert.Equal("0", rows[3][3]);
        Assert.Equal(.05, double.Parse(rows[3][8], CultureInfo.InvariantCulture), 12);
    }

    [Fact]
    public void BaselineAppliesToTheFirstSelectedConnectionOnlyAndWindowEdgesAreInclusive()
    {
        using var input = Input("0.01,1,0,110,0\n2.00,2,0,110,0\n2.01,2,0,120,0\n3.00,3,0,120,0\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var count = ReplayCommand.Run(input, output,
            Options("--baseline", "110", "--max-correction", "10", "--from", "2", "--to", "2.01"), CancellationToken.None);
        var rows = Rows(output);
        Assert.Equal(2, count);
        Assert.Equal("2", rows[1][1]);
        Assert.Equal("10", rows[1][3]);
        Assert.Equal("0", rows[1][8]);
        Assert.Equal(.1, double.Parse(rows[2][8], CultureInfo.InvariantCulture), 12);
    }

    [Theory]
    [InlineData("throttle", "150")]
    [InlineData("clutch", "175")]
    public void SelectsTheRequestedAxis(string axis, string expected)
    {
        using var input = Input("0.01,1,150,110,175\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        ReplayCommand.Run(input, output, Options("--axis", axis), CancellationToken.None);
        var rows = Rows(output);
        Assert.Equal(axis + "_device_axis", rows[0][2]);
        Assert.Equal(expected, rows[1][2]);
    }

    [Fact]
    public void PreservesReportsWithEqualRoundedTimestamps()
    {
        using var input = Input("0.01,1,0,100,0\n0.01,1,0,110,0\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(2, ReplayCommand.Run(input, output, Options(), CancellationToken.None));
    }

    [Theory]
    [InlineData("0.01,1,0,100,\n")]
    [InlineData("0.01,1,0,100\n")]
    [InlineData("0.01,1,0,100,0,extra\n")]
    [InlineData("NaN,1,0,100,0\n")]
    [InlineData("0.01,0,0,100,0\n")]
    [InlineData("0.01,1,0,-1,0\n")]
    [InlineData("0.01,1,0,16777217,0\n")]
    [InlineData("0.01,1,16385,100,0\n")]
    [InlineData("0.01,1,0,100,16385\n")]
    [InlineData("0.02,1,0,100,0\n0.01,1,0,100,0\n")]
    [InlineData("0.01,2,0,100,0\n0.02,1,0,100,0\n")]
    public void RejectsMalformedTruncatedOrReorderedCaptures(string rows)
    {
        using var input = Input(rows);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Throws<InvalidDataException>(() => ReplayCommand.Run(input, output, Options(), CancellationToken.None));
    }

    [Theory]
    [InlineData("wrong header\n0.01,1,0,100,0")]
    [InlineData(ReplaySample.Header + "\n")]
    public void RejectsMissingSchemaOrEmptyData(string contents)
    {
        using var input = new StringReader(contents);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Throws<InvalidDataException>(() => ReplayCommand.Run(input, output, Options(), CancellationToken.None));
    }

    [Fact]
    public void ValidatesRowsOutsideTheSelectedWindow()
    {
        using var input = Input("0.01,1,0,100,0\n2.00,1,0,\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Throws<InvalidDataException>(() => ReplayCommand.Run(input, output, Options("--to", "1"), CancellationToken.None));
    }

    [Fact]
    public void CancelsBeforeProducingOutput()
    {
        using var input = Input("0.01,1,0,100,0\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ReplayCommand.Run(input, output, Options(), cancellation.Token));
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void CancellationDuringReplayStopsBeforeTheNextSample()
    {
        using var input = Input("0.01,1,0,100,0\n0.02,1,0,110,0\n0.03,1,0,120,0\n");
        using var cancellation = new CancellationTokenSource();
        using var output = new CancellingWriter(cancellation);
        Assert.Throws<OperationCanceledException>(() => ReplayCommand.Run(input, output, Options(), cancellation.Token));
        Assert.Equal(3, Rows(output).Length);
    }

    [Fact]
    public void ReplayUsesInvariantNumbersUnderAnotherCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var input = Input("0.01,1,0,110,0\n");
            using var output = new StringWriter(CultureInfo.CurrentCulture);
            ReplayCommand.Run(input, output, Options("--exponent", "2.0"), CancellationToken.None);
            var rows = Rows(output);
            Assert.Equal(9, rows[1].Length);
            Assert.Equal(.01, double.Parse(rows[1][8], CultureInfo.InvariantCulture), 12);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void RejectsAnIntervalWithNoSamples()
    {
        using var input = Input("0.01,1,0,100,0\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Throws<InvalidDataException>(() => ReplayCommand.Run(input, output, Options("--from", "1"), CancellationToken.None));
    }

    [Fact]
    public void StorageFailureDoesNotReportSuccessfulReplay()
    {
        using var input = Input("0.01,1,0,100,0\n");
        using var output = new FailedWriter();
        Assert.Throws<IOException>(() => ReplayCommand.Run(input, output, Options(), CancellationToken.None));
    }

    [Fact]
    public void ExistingOutputIsNeverOverwritten()
    {
        var inputPath = Path.Combine(AppContext.BaseDirectory, $"replay-input-{Guid.NewGuid():N}.csv");
        var outputPath = Path.Combine(AppContext.BaseDirectory, $"replay-output-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(inputPath, ReplaySample.Header + "\n0.01,1,0,100,0\n");
            File.WriteAllText(outputPath, "keep");
            var options = Options() with { InputPath = inputPath, OutputPath = outputPath };
            Assert.Throws<IOException>(() => ReplayCommand.Execute(options));
            Assert.Equal("keep", File.ReadAllText(outputPath));
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void RejectsInvalidOrUnsafeReplayOptions()
    {
        string[][] invalid =
        [
            ["--baseline", "95"], ["--max-correction", "10"],
            ["--baseline", "50", "--max-correction", "10"],
            ["--max-correction", "NaN", "--baseline", "95"],
            ["--exponent", "0"], ["--deadzone-low", ".5", "--deadzone-high", ".5"],
            ["--from", "2", "--to", "1"], ["--from", "-1"], ["--to", "Infinity"],
            ["--axis", "invalid"], ["--invert", "yes"], ["--unknown", "1"],
            ["--from", "1", "--from", "2"], ["--to"]
        ];
        foreach (var options in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => Options(options));
        }

        Assert.Throws<ArgumentException>(() => ReplayOptions.Parse([]));
    }

    private static ReplayOptions Options(params string[] extra) => ReplayOptions.Parse(
        ["--input", "unused-input.csv", "--csv", "unused-output.csv", "--minimum", "100", "--maximum", "200", .. extra]);

    private static StringReader Input(string rows) => new(ReplaySample.Header + "\n" + rows);

    private static string[][] Rows(StringWriter output) => output.ToString()
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(',')).ToArray();

    private sealed class FailedWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("Simulated disk failure.");
    }

    private sealed class CancellingWriter(CancellationTokenSource cancellation) : StringWriter(CultureInfo.InvariantCulture)
    {
        private int lines;

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (++lines == 3)
            {
                cancellation.Cancel();
            }
        }
    }
}
