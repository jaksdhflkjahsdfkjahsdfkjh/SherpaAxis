using System.IO;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;

namespace SherpaAxis.App.Tests;

public sealed class ProfileLibraryTests
{
    [Fact]
    public async Task WindowsPathCasingAndTrailingSeparatorsDoNotCreateDuplicateProfiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new ProfileLibrary(directory + Path.DirectorySeparatorChar);
            var path = await library.SaveAsync(DashboardEngine.DefaultProfile());
            var samePath = await library.SaveAsync(DashboardEngine.DefaultProfile(), path.ToUpperInvariant());
            Assert.Equal(path, samePath, ignoreCase: true);
            Assert.Single(Directory.GetFiles(directory, "*.json"));
            await library.RememberAsync(path.ToUpperInvariant());
            Assert.Equal(path, await library.SelectedPathAsync(), ignoreCase: true);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Fact]
    public async Task MarkerCleanupFailureNeverDeletesTheProfile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-marker-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(directory, "autoload-profile");
        try
        {
            var library = new ProfileLibrary(directory);
            var path = await library.SaveAsync(DashboardEngine.DefaultProfile());
            await library.SetAutoLoadAsync(path);
            File.SetAttributes(marker, FileAttributes.ReadOnly);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.DeleteAsync(path));
            Assert.True(File.Exists(path));
            Assert.Equal(path, await library.AutoLoadPathAsync());
            Assert.Equal(path, await library.SelectedPathAsync());
        }
        finally
        {
            if (File.Exists(marker)) { File.SetAttributes(marker, FileAttributes.Normal); }
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    }
}
