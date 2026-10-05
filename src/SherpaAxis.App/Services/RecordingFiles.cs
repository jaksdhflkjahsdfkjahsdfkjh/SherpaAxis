using System.IO;

namespace SherpaAxis.App.Services;

internal sealed class RecordingFiles
{
    private readonly List<FileStream> streams = [];
    private readonly List<StreamWriter> writers = [];

    public TextWriter Input => writers[0];
    public TextWriter Processed => writers[1];
    public TextWriter Events => writers[2];

    public static RecordingFiles Create(string inputPath)
    {
        if (!string.Equals(Path.GetExtension(inputPath), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The recording input file must have a .csv extension.", nameof(inputPath));
        }

        var paths = new[] { Path.GetFullPath(inputPath), Path.ChangeExtension(Path.GetFullPath(inputPath), ".processed.csv"),
            Path.ChangeExtension(Path.GetFullPath(inputPath), ".session.jsonl") };
        var files = new RecordingFiles();
        try
        {
            foreach (var path in paths)
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
                files.streams.Add(stream);
                files.writers.Add(new StreamWriter(stream, bufferSize: 16384, leaveOpen: true));
            }

            return files;
        }
        catch
        {
            // Only files opened by this attempt are owned; existing companions are never touched.
            for (var index = 0; index < files.streams.Count; index++)
            {
                files.streams[index].Dispose();
                try { File.Delete(paths[index]); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }

            foreach (var writer in files.writers)
            {
                try { writer.Dispose(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
            }

            throw;
        }
    }

    public async Task<Exception?> CloseAsync()
    {
        Exception? failure = null;
        // Close unbuffered streams first so a failed writer cannot retry buffered I/O without cancellation.
        foreach (var stream in streams)
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failure ??= exception; }
        }

        foreach (var writer in writers)
        {
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failure ??= exception; }
        }

        return failure;
    }
}
