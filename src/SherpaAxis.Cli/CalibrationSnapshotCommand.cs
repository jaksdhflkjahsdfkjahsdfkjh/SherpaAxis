using System.Text.Json;
using SherpaAxis.FeelVR;

namespace SherpaAxis.Cli;

internal static class CalibrationSnapshotCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task RunAsync(CalibrationSnapshotReader reader, string[] arguments)
    {
        string? jsonPath = null;
        string? devicePath = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var option = arguments[index];
            if (index + 1 >= arguments.Length || !seen.Add(option) || string.IsNullOrWhiteSpace(arguments[index + 1]))
            {
                throw new ArgumentException($"Missing or repeated snapshot option: {option}");
            }

            switch (option)
            {
                case "--json": jsonPath = arguments[index + 1]; break;
                case "--device": devicePath = arguments[index + 1]; break;
                default: throw new ArgumentException($"Unknown snapshot option: {option}");
            }
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, cancel) =>
        {
            cancel.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var snapshot = await reader.ReadAsync(TimeSpan.FromSeconds(2), devicePath, cancellation.Token).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            if (jsonPath is not null)
            {
                await using var stream = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await using var writer = new StreamWriter(stream);
                await writer.WriteLineAsync(json.AsMemory(), cancellation.Token).ConfigureAwait(false);
            }

            Console.WriteLine(json);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
