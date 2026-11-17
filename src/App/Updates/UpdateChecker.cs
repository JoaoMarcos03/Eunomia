using Eunomia.App.Settings;

namespace Eunomia.App.Updates;

/// <summary>A newer release to tell the user about.</summary>
/// <param name="Tag">Its tag, which "Skip this version" records.</param>
public sealed record AvailableUpdate(ReleaseVersion Version, string Tag, Uri Url);

/// <summary>
/// The update check (request of 16 Nov 2026): at startup, at most once a day, asks the feed for the releases and keeps
/// the newest one this build may move to in <see cref="UpdateOptions"/>; the notice comes from what is kept, so it shows
/// at every start until the user updates or skips that version. Pre-releases are offered only to pre-release builds.
/// Methods that change <see cref="UpdateOptions"/> run their continuations where they were called (the UI thread).
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>The least time between two questions to GitHub at startup.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IReleaseFeed _feed;
    private readonly TimeProvider _clock;

    public UpdateChecker(IReleaseFeed feed, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(feed);
        _feed = feed;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// At startup: nothing when the user turned the check off; GitHub only when the last answer is a day old; then the
    /// kept release, if it is newer than <paramref name="current"/> and not skipped. Feed errors propagate and record nothing.
    /// </summary>
    public async Task<AvailableUpdate?> CheckAtStartupAsync(UpdateOptions options, ReleaseVersion current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(current);
        if (!options.CheckOnStartup)
        {
            return null;
        }
        if (IsDue(options, _clock.GetUtcNow()))
        {
            await RefreshAsync(options, current, cancellationToken);
        }
        return Offer(options, current, includeSkipped: false);
    }

    /// <summary>Help › Check for updates: always asks GitHub, and a skipped version is offered again.</summary>
    public async Task<AvailableUpdate?> CheckNowAsync(UpdateOptions options, ReleaseVersion current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(current);
        await RefreshAsync(options, current, cancellationToken);
        return Offer(options, current, includeSkipped: true);
    }

    /// <summary>A day since the last answer, none yet, or a last answer in the future (the clock went back).</summary>
    public static bool IsDue(UpdateOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.LastCheck is not { } last || now - last >= Interval || last - now > TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// The newest release <paramref name="current"/> may move to: published (no drafts), with a tag in our format, a
    /// trusted page and, for a release build, no pre-releases. <c>null</c> when there is none.
    /// </summary>
    public static ReleaseInfo? SelectLatest(IEnumerable<ReleaseInfo> releases, ReleaseVersion current)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(current);
        ReleaseInfo? best = null;
        ReleaseVersion? bestVersion = null;
        foreach (var release in releases)
        {
            if (release.IsDraft || !ReleasePages.IsTrusted(release.Url) || ReleaseVersion.TryParse(release.Tag) is not { } version)
            {
                continue;
            }
            if ((release.IsPrerelease || version.IsPrerelease) && !current.IsPrerelease)
            {
                continue;
            }
            if (bestVersion is null || version > bestVersion)
            {
                best = release;
                bestVersion = version;
            }
        }
        return best;
    }

    /// <summary>The kept release as a notice: newer than <paramref name="current"/>, allowed for it and, unless asked, not skipped.</summary>
    public static AvailableUpdate? Offer(UpdateOptions options, ReleaseVersion current, bool includeSkipped)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(current);
        if (options.LatestTag is not { } tag
            || ReleaseVersion.TryParse(tag) is not { } latest
            || !latest.IsNewerThan(current)
            || (latest.IsPrerelease && !current.IsPrerelease)
            || !Uri.TryCreate(options.LatestUrl, UriKind.Absolute, out var url)
            || !ReleasePages.IsTrusted(url))
        {
            return null;
        }
        if (!includeSkipped && ReleaseVersion.TryParse(options.SkippedTag) is { } skipped && latest <= skipped)
        {
            return null;
        }
        return new AvailableUpdate(latest, tag, url);
    }

    /// <summary>"Skip this version": no notice for it or older ones; a newer release shows again.</summary>
    public static void Skip(UpdateOptions options, AvailableUpdate update)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(update);
        options.SkippedTag = update.Tag;
    }

    private async Task RefreshAsync(UpdateOptions options, ReleaseVersion current, CancellationToken cancellationToken)
    {
        var releases = await _feed.GetReleasesAsync(cancellationToken);
        var latest = SelectLatest(releases, current);
        options.LastCheck = _clock.GetUtcNow();
        options.LatestTag = latest?.Tag;
        options.LatestUrl = latest?.Url.AbsoluteUri;
    }
}
