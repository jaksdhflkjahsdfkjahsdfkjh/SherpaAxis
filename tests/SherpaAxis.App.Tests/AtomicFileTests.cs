using System.IO;
using System.Text;
using SherpaAxis.App.Services;

namespace SherpaAxis.App.Tests;

public sealed class AtomicFileTests
{
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("replacement"), cancelled.Token));
            Assert.Equal("short", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }
}
