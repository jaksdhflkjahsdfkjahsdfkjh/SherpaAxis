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
        // TextWriter can block synchronously even through its async API.
        worker = Task.Factory.StartNew(() =>
        {
            try
            {
                errors.WriteLineAsync("Firmware-calibrated device axes; ADC RAW is unavailable. No device writes.").GetAwaiter().GetResult();
                while (messages.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (messages.Reader.TryRead(out var message))
                    {
                        (message.IsError ? errors : output).WriteLineAsync(message.Text).GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Loss of a display must not terminate an otherwise valid recording.
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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
