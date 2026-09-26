using System.Threading.Channels;

namespace SherpaAxis.Cli;

internal sealed class CaptureDisplay
{
    private readonly Channel<(string Text, bool IsError)> messages = Channel.CreateBounded<(string, bool)>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
    private readonly Task worker;

    public Task Completion => worker;

    public CaptureDisplay(TextWriter output, TextWriter errors)
    {
        worker = Task.Run(async () =>
        {
            try
            {
                await errors.WriteLineAsync("Firmware-calibrated device axes; ADC RAW is unavailable. No device writes.").ConfigureAwait(false);
                await foreach (var message in messages.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await (message.IsError ? errors : output).WriteLineAsync(message.Text).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Loss of a display must not terminate an otherwise valid recording.
            }
        });
    }

    public void Publish(string text, bool isError = false) => messages.Writer.TryWrite((text, isError));

    public async Task CompleteAsync()
    {
        messages.Writer.TryComplete();
        // A terminal write cannot be cancelled reliably. It must not hold capture shutdown open.
        if (await Task.WhenAny(worker, Task.Delay(TimeSpan.FromMilliseconds(250))).ConfigureAwait(false) == worker)
        {
            await worker.ConfigureAwait(false);
        }
    }
}
