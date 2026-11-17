using System.Text.Json;
using System.Text.Json.Serialization;
using Eunomia.App.Updates;

namespace Eunomia.Infra.Updates;

/// <summary>
/// The project's releases from the GitHub REST API: one anonymous GET, with only "Eunomia/&lt;version&gt;" as the
/// User-Agent (GitHub requires one). No account, token, identifier or data about the computer is sent; GitHub sees the
/// IP address like for any web request.
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    /// <summary>The 30 most recent releases (pre-releases included; drafts only show to the repository's owners).</summary>
    public static Uri DefaultUri { get; } = new("https://api.github.com/repos/JoaoMarcos03/Eunomia/releases?per_page=30");

    /// <summary>A list of 30 releases is some tens of KB; anything above this is not GitHub's answer.</summary>
    internal const int MaxResponseBytes = 4 * 1024 * 1024;

    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly Uri _uri;

    /// <param name="version">The running version, for the User-Agent.</param>
    /// <param name="handler">Tests: a handler that answers without the network (not disposed here).</param>
    /// <param name="uri">Tests: another address.</param>
    public GitHubReleaseFeed(string version, HttpMessageHandler? handler = null, Uri? uri = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout;
        _http.MaxResponseContentBufferSize = MaxResponseBytes;
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("Eunomia/" + (string.IsNullOrWhiteSpace(version) ? "0" : version.Trim()));
        _http.DefaultRequestHeaders.Accept.TryParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        _uri = uri ?? DefaultUri;
    }

    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(_uri, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return Parse(body);
    }

    /// <summary>The releases with a tag and a trusted page; throws <see cref="JsonException"/> when it is not a list.</summary>
    internal static IReadOnlyList<ReleaseInfo> Parse(ReadOnlySpan<byte> json)
    {
        var items = JsonSerializer.Deserialize(json, GitHubJsonContext.Default.ListGitHubRelease) ?? [];
        var releases = new List<ReleaseInfo>(items.Count);
        foreach (var item in items)
        {
            if (item is { TagName: { Length: > 0 } tag }
                && Uri.TryCreate(item.HtmlUrl, UriKind.Absolute, out var url)
                && ReleasePages.IsTrusted(url))
            {
                releases.Add(new ReleaseInfo(tag, url, item.Prerelease, item.Draft));
            }
        }
        return releases;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>The fields of a GitHub release the check uses; the others are ignored.</summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }
}

[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
