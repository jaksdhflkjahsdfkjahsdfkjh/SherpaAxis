using System.Globalization;

namespace SherpaAxis.Cli;

internal static class ReplayCommand
{
    public static async Task ExecuteAsync(ReplayOptions options)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, cancel) =>
        {
            cancel.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            options = await options.ResolveAsync(cancellation.Token).ConfigureAwait(false);
            using var input = new StreamReader(options.InputPath);
            using var file = new FileStream(options.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var output = new StreamWriter(file);
            var count = Run(input, output, options, cancellation.Token);
            output.Flush();
            Console.WriteLine(FormattableString.Invariant($"Replay complete: {count} {options.Axis} samples. Output is offline software processing; no device changes."));
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    public static long Run(TextReader input, TextWriter output, ReplayOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processor = options.CreateProcessor();
        var connection = 0;
        long count = 0;
        output.WriteLine($"elapsed_seconds,connection,{options.Axis}_device_axis,baseline_correction,corrected_input,normalized,corrected_normalized,after_deadzone,output,filtered_input,filtered_normalized,filter_reset");
        foreach (var sample in ReplaySample.Read(input, cancellationToken))
        {
            if (sample.ElapsedSeconds < options.From || sample.ElapsedSeconds > options.To)
            {
                continue;
            }

            if (connection != sample.Connection)
            {
                processor.ResetBaseline();
                if (count == 0 && options.Baseline.HasValue)
                {
                    processor.ConfirmReleasedBaseline(options.Baseline.Value);
                }

                connection = sample.Connection;
            }

            var value = options.Axis switch
            {
                "throttle" => sample.Axes.Throttle,
                "clutch" => sample.Axes.Clutch,
                _ => sample.Axes.Brake
            };
            var result = processor.Process(value, sample.ElapsedSeconds);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{sample.ElapsedSeconds:R},{connection},{value},{result.BaselineCorrection:R},{result.CorrectedInput:R},{result.Normalized:R},{result.CorrectedNormalized:R},{result.AfterDeadzone:R},{result.Output:R},{result.FilteredInput:R},{result.FilteredNormalized:R},{(result.FilterReset ? 1 : 0)}"));
            count++;
        }

        if (count == 0)
        {
            throw new InvalidDataException("No samples in the selected replay interval.");
        }

        return count;
    }
}
