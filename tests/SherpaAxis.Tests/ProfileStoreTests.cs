using SherpaAxis.Profiles;

namespace SherpaAxis.Tests;

public sealed class ProfileStoreTests
{
    [Fact]
    public async Task SaveLoadExportAndExplicitReplacementPreserveCompleteDocuments()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "profile.json");
        var export = Path.Combine(directory.Path, "export.json");
        await ProfileStore.SaveAsync(path, ProfileJsonTests.Profile());
        var original = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<IOException>(() => ProfileStore.SaveAsync(path, ProfileJsonTests.Profile("Other")));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        await ProfileStore.SaveAsync(export, await ProfileStore.LoadAsync(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(export));
        await ProfileStore.SaveAsync(path, ProfileJsonTests.Profile("Other"), overwrite: true);
        Assert.Equal("Other", (await ProfileStore.LoadAsync(path)).Name);
        Assert.Equal("Test", (await ProfileStore.LoadAsync(export)).Name);
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task CancelledSaveLeavesTheExistingFileUntouchedAndCreatesNoTemporaryFile()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "profile.json");
        await ProfileStore.SaveAsync(path, ProfileJsonTests.Profile());
        var original = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProfileStore.SaveAsync(path, ProfileJsonTests.Profile("Other"), true, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProfileStore.LoadAsync(path, cancellation.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task FailedFinalRenameCleansTheTemporaryFileAndPreservesTheDestination()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "existing-directory");
        Directory.CreateDirectory(destination);
        var marker = Path.Combine(destination, "keep.txt");
        try
        {
            await File.WriteAllTextAsync(marker, "keep");
            var error = await Record.ExceptionAsync(() => ProfileStore.SaveAsync(destination, ProfileJsonTests.Profile(), true));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal("keep", await File.ReadAllTextAsync(marker));
            Assert.Empty(Directory.GetFiles(directory.Path));
        }
        finally
        {
            File.Delete(marker);
            Directory.Delete(destination);
        }
    }

    [Fact]
    public async Task ConcurrentCreationAllowsOneCompleteFileWithoutOverwritingTheWinner()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "profile.json");
        async Task<bool> Save(string name)
        {
            try
            {
                await ProfileStore.SaveAsync(path, ProfileJsonTests.Profile(name));
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Save("First"), Save("Second"));
        Assert.Single(results, success => success);
        Assert.Equal(results[0] ? "First" : "Second", (await ProfileStore.LoadAsync(path)).Name);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task InvalidAndOversizedFilesAreRejectedWithoutRewritingThem()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "bad.json");
        await File.WriteAllTextAsync(path, "{invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => ProfileStore.LoadAsync(path));
        Assert.Equal("{invalid", await File.ReadAllTextAsync(path));
        using (var stream = new FileStream(path, FileMode.Create))
        {
            stream.SetLength(ProfileJson.MaximumFileBytes + 1);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => ProfileStore.LoadAsync(path));
        Assert.Equal(ProfileJson.MaximumFileBytes + 1, new FileInfo(path).Length);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, $"profiles-{Guid.NewGuid():N}");

        public TestDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Path))
            {
                File.Delete(file);
            }

            Directory.Delete(Path);
        }
    }
}
