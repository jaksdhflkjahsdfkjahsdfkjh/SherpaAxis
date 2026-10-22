using System.IO;
using System.Net;
using System.Net.Http;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.Services.Updates;

namespace SherpaAxis.App.Tests;

public sealed class AppUpdateCheckerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-updates-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(directory, "updates.json");

    [Theory]
    [InlineData("v1.0", false)]
    [InlineData("1.0.0", false)]
    [InlineData("V1.0.0.0", false)]
    [InlineData("v0.9.9", false)]
    [InlineData("v1.0.1", true)]
    [InlineData("v1.10.0", true)]
    [InlineData("v2.0", true)]
    [InlineData("v2.0-rc.1", false)]
    [InlineData("v2", false)]
    [InlineData("https://example.com", false)]
    public void ComparesNumericStableVersions(string tag, bool newer) =>
        Assert.Equal(newer, AppUpdateChecker.IsNewer(new(tag), new Version(1, 0)));

    [Fact]
    public void DoubleDigitMinorVersionIsNotComparedAsText() =>
        Assert.True(AppUpdateChecker.IsNewer(new("v1.10.0"), new Version(1, 9, 0)));

    [Fact]
    public async Task RequestsOnlyPublicMetadataAndBuildsItsOwnTrustedReleaseLink()
    {
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(ApplicationLinks.LatestReleaseApi, request.RequestUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Content);
            Assert.StartsWith("SherpaAxis/", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString(), StringComparison.Ordinal);
            Assert.Equal("2026-03-10", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            return Task.FromResult(Json("""{"tag_name":"v1.1.0","draft":false,"prerelease":false,"html_url":"https://untrusted.example/download.exe"}"""));
        });
        using var client = new HttpClient(handler);
        var release = Assert.IsType<AppRelease>(await new AppUpdateChecker(client, StatePath).CheckAsync());
        Assert.Equal("v1.1.0", release.DisplayVersion);
        Assert.Equal($"https://github.com/{ApplicationLinks.Repository}/releases/tag/v1.1.0", release.Page.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{"tag_name":"v2.0","draft":true,"prerelease":false}""")]
    [InlineData("""{"tag_name":"v2.0","draft":false,"prerelease":true}""")]
    [InlineData("""{"tag_name":"v2.0-beta","draft":false,"prerelease":false}""")]
    [InlineData("""{"tag_name":"../../elsewhere","draft":false,"prerelease":false}""")]
    public async Task IgnoresDraftsPreviewsAndInvalidTags(string body)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(body)));
        using var client = new HttpClient(handler);
        Assert.Null(await new AppUpdateChecker(client, StatePath).CheckAsync());
    }

    [Fact]
    public async Task EachCheckFetchesFreshMetadataIncludingAfterRestart()
    {
        var tag = "v1.1";
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(ReleaseJson(tag))));
        using var client = new HttpClient(handler);
        var first = new AppUpdateChecker(client, StatePath);
        Assert.Equal("v1.1", (await first.CheckAsync())?.Tag);
        tag = "v1.2";
        Assert.Equal("v1.2", (await first.CheckAsync())?.Tag);
        Assert.Equal(2, handler.Requests);
        tag = "v1.3";
        Assert.Equal("v1.3", (await new AppUpdateChecker(client, StatePath).CheckAsync())?.Tag);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task MissingReleaseDoesNotBecomeAnUpdateOrSuppressTheNextCheck()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new HttpClient(handler);
        Assert.Null(await new AppUpdateChecker(client, StatePath).CheckAsync());
        Assert.Null(await new AppUpdateChecker(client, StatePath).CheckAsync());
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Found)]
    public async Task FailedRequestsAreNotCached(HttpStatusCode status)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var client = new HttpClient(handler);
        var checker = new AppUpdateChecker(client, StatePath);
        await Assert.ThrowsAsync<HttpRequestException>(() => checker.CheckAsync());
        Assert.False(File.Exists(StatePath));
        await Assert.ThrowsAsync<HttpRequestException>(() => checker.CheckAsync());
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"tag_name\":3}")]
    [InlineData("{\"tag_name\":\"v2.0\",\"draft\":false}")]
    [InlineData("{\"tag_name\":\"v2.0\",\"draft\":\"false\",\"prerelease\":false}")]
    public async Task RejectsIncompleteMetadata(string body)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(body)));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppUpdateChecker(client, StatePath).CheckAsync());
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task RejectsOversizedMetadata()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(new string(' ', 1_048_577))));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppUpdateChecker(client, StatePath).CheckAsync());
    }

    [Fact]
    public async Task CorruptCacheIsReplacedAndNotificationsRemainDeduplicatedAcrossRestarts()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(StatePath, "{invalid");
        var tag = "v1.1";
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(ReleaseJson(tag))));
        using var client = new HttpClient(handler);
        var first = new AppUpdateChecker(client, StatePath);
        var release = Assert.IsType<AppRelease>(await first.CheckAsync());
        Assert.True(await first.MarkNotifiedAsync(release));
        var restarted = new AppUpdateChecker(client, StatePath);
        Assert.Equal(release, await restarted.CheckAsync());
        Assert.False(await restarted.MarkNotifiedAsync(release));
        tag = "v1.2";
        var next = Assert.IsType<AppRelease>(await restarted.CheckAsync());
        Assert.True(await restarted.MarkNotifiedAsync(next));
        Assert.False(await restarted.MarkNotifiedAsync(next));
        Assert.False(await restarted.MarkNotifiedAsync(release));
    }

    [Fact]
    public async Task CancellationStopsTheRequestWithoutWritingTheCache()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(ReleaseJson("v2.0"));
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var check = new AppUpdateChecker(client, StatePath).CheckAsync(cancellation.Token);
        await started.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task OlderSettingsEnableChecksAndOptOutSurvivesRestart()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1,\"Language\":\"uk\",\"TourDismissed\":true}");
        var store = new ApplicationPreferencesStore(path);
        var old = await store.LoadAsync(CancellationToken.None);
        Assert.True(old.CheckForUpdates);
        await store.SaveAsync(old with { CheckForUpdates = false }, CancellationToken.None);
        var saved = await store.LoadAsync(CancellationToken.None);
        Assert.False(saved.CheckForUpdates);
        Assert.Equal("uk", saved.Language);
        Assert.True(saved.TourDismissed);
    }

    internal static string ReleaseJson(string tag) => $"{{\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":false}}";
    internal static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return send(request, cancellationToken); }
    }
    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
}
