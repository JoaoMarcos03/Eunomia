using System.Net;
using System.Text;
using System.Text.Json;
using Eunomia.Infra.Updates;

namespace Eunomia.Infra.Updates.Tests;

/// <summary>The GitHub releases request (update check): what it sends, what it reads, and what it refuses.</summary>
public sealed class GitHubReleaseFeedTests
{
    private const string Page = "https://github.com/JoaoMarcos03/Eunomia/releases/tag/";

    /// <summary>A trimmed answer of the API, with fields the check does not use.</summary>
    private const string Answer = $$"""
        [
          { "id": 3, "tag_name": "v1.0.0-beta.2-build.21", "name": "1.0.0-beta.2", "html_url": "{{Page}}v1.0.0-beta.2-build.21",
            "prerelease": true, "draft": false, "author": { "login": "x" }, "assets": [ { "size": 1 } ], "body": "notes" },
          { "tag_name": "v1.0.0-build.30", "html_url": "{{Page}}v1.0.0-build.30", "prerelease": false, "draft": false },
          { "tag_name": "v1.1.0-build.31", "html_url": "{{Page}}untagged-1", "draft": true },
          { "tag_name": "v9.0.0-build.99", "html_url": "https://example.com/JoaoMarcos03/Eunomia/releases/tag/v9" },
          { "tag_name": "", "html_url": "{{Page}}v1" },
          { "html_url": "{{Page}}v2" },
          { "tag_name": "v3.0.0", "html_url": null }
        ]
        """;

    [Fact]
    public void ReadsTheTagPageAndFlagsAndDropsTheRest()
    {
        var releases = GitHubReleaseFeed.Parse(Encoding.UTF8.GetBytes(Answer));

        Assert.Equal(3, releases.Count);
        Assert.Equal(("v1.0.0-beta.2-build.21", true, false), (releases[0].Tag, releases[0].IsPrerelease, releases[0].IsDraft));
        Assert.Equal(Page + "v1.0.0-beta.2-build.21", releases[0].Url.AbsoluteUri);
        Assert.Equal(("v1.0.0-build.30", false, false), (releases[1].Tag, releases[1].IsPrerelease, releases[1].IsDraft));
        Assert.Equal(("v1.1.0-build.31", false, true), (releases[2].Tag, releases[2].IsPrerelease, releases[2].IsDraft));
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("null", 0)]
    public void AnEmptyAnswerHasNoReleases(string json, int count) =>
        Assert.Equal(count, GitHubReleaseFeed.Parse(Encoding.UTF8.GetBytes(json)).Count);

    [Theory]
    [InlineData("""{ "message": "API rate limit exceeded" }""")]
    [InlineData("<html>")]
    [InlineData("")]
    [InlineData("""[ { "tag_name": 5 } ]""")]
    public void AnythingButAListIsAnError(string json) =>
        Assert.ThrowsAny<JsonException>(() => GitHubReleaseFeed.Parse(Encoding.UTF8.GetBytes(json)));

    /// <summary>One anonymous GET to the project's releases: no token, cookie or anything about the computer.</summary>
    [Fact]
    public async Task SendsOnlyAnAnonymousGetWithTheVersion()
    {
        using var handler = new StubHandler(_ => Json(Answer));
        using var feed = new GitHubReleaseFeed("1.0.0-beta.2+5f3ddb37", handler);

        var releases = await feed.GetReleasesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, releases.Count);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(GitHubReleaseFeed.DefaultUri, request.RequestUri);
        Assert.Equal("api.github.com", request.RequestUri!.Host);
        Assert.Equal(Uri.UriSchemeHttps, request.RequestUri.Scheme);
        Assert.Equal("Eunomia/1.0.0-beta.2+5f3ddb37", request.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.Select(a => a.MediaType));
        Assert.Equal(["2022-11-28"], request.Headers.GetValues("X-GitHub-Api-Version"));
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Equal(["Accept", "User-Agent", "X-GitHub-Api-Version"], request.Headers.Select(h => h.Key).Order(StringComparer.Ordinal));
        Assert.Null(request.Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnUnknownVersionStillSendsAUserAgent(string version)
    {
        using var handler = new StubHandler(_ => Json("[]"));
        using var feed = new GitHubReleaseFeed(version, handler);

        await feed.GetReleasesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Eunomia/0", Assert.Single(handler.Requests).Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnErrorStatusThrows(HttpStatusCode status)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("""{ "message": "no" }""") });
        using var feed = new GitHubReleaseFeed("1.0.0", handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => feed.GetReleasesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task AnOversizedAnswerIsRefused()
    {
        var huge = "[" + string.Join(',', Enumerable.Repeat("""{ "tag_name": "v1.0.0", "body": "x" }""", 1 + (GitHubReleaseFeed.MaxResponseBytes / 30))) + "]";
        using var handler = new StubHandler(_ => Json(huge));
        using var feed = new GitHubReleaseFeed("1.0.0", handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => feed.GetReleasesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAnswerThatIsNotAListThrows()
    {
        using var handler = new StubHandler(_ => Json("""{ "message": "Moved Permanently" }"""));
        using var feed = new GitHubReleaseFeed("1.0.0", handler);

        await Assert.ThrowsAnyAsync<JsonException>(() => feed.GetReleasesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACancelledRequestThrows()
    {
        using var handler = new StubHandler(_ => Json("[]"));
        using var feed = new GitHubReleaseFeed("1.0.0", handler);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => feed.GetReleasesAsync(cancel.Token));
    }

    [Fact]
    public void TheRequestHasATimeoutAndALimit()
    {
        Assert.InRange(GitHubReleaseFeed.Timeout, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        Assert.InRange(GitHubReleaseFeed.MaxResponseBytes, 256 * 1024, 16 * 1024 * 1024);
        Assert.Equal("https://api.github.com/repos/JoaoMarcos03/Eunomia/releases?per_page=30", GitHubReleaseFeed.DefaultUri.AbsoluteUri);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }
}
