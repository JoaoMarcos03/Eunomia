namespace Eunomia.App.Updates;

/// <summary>A published release as the feed lists it.</summary>
/// <param name="Tag">The git tag ("v1.0.0-beta.2-build.15").</param>
/// <param name="Url">The release page; only <see cref="ReleasePages.IsTrusted"/> ones are kept and opened.</param>
public sealed record ReleaseInfo(string Tag, Uri Url, bool IsPrerelease, bool IsDraft);

/// <summary>
/// Port for the list of releases (the update check). The implementation is in <c>Infra.Updates</c>: one anonymous
/// HTTPS request to the GitHub API, nothing about the user or the computer sent.
/// </summary>
public interface IReleaseFeed
{
    /// <summary>The most recent releases; throws on network or format errors.</summary>
    Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken);
}

/// <summary>The pages the notice may open: the project's releases on GitHub, over HTTPS.</summary>
public static class ReleasePages
{
    /// <summary>The project's releases (Help › Check for updates opens it when there is nothing newer).</summary>
    public static Uri Releases { get; } = new("https://github.com/JoaoMarcos03/Eunomia/releases");

    public static bool IsTrusted(Uri? url) =>
        url is { IsAbsoluteUri: true }
        && url.Scheme == Uri.UriSchemeHttps
        && url.UserInfo.Length == 0
        && string.Equals(url.Host, Releases.Host, StringComparison.OrdinalIgnoreCase)
        && url.IsDefaultPort
        && url.AbsolutePath.StartsWith(Releases.AbsolutePath + "/", StringComparison.OrdinalIgnoreCase);
}
