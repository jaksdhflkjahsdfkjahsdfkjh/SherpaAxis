using System.IO;
using System.Text;
using SherpaAxis.App.Services.Persistence;

namespace SherpaAxis.App.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public async Task ReplacingAnOpenSnapshotPreservesBothTheReaderAndTheNewDocument()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-snapshot-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "status.json");
        try
        {
            await AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("original snapshot"));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                await AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("new snapshot"));
                Assert.Equal("original snapshot", await reader.ReadToEndAsync());
                Assert.Equal("new snapshot", await File.ReadAllTextAsync(path));
                await Assert.ThrowsAsync<IOException>(() => AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("must not replace"), overwrite: false));
                Assert.Equal("new snapshot", await File.ReadAllTextAsync(path));
            }
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Fact]
    public async Task WritesReplaceTheWholeDocumentAndCancelledWritesPreserveIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-atomic-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "nested", "data.json");
        try
        {
            await AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("long original document"));
            await AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("short"));
            Assert.Equal("short", await File.ReadAllTextAsync(path));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("replacement"), token: cancelled.Token));
            Assert.Equal("short", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }
}
