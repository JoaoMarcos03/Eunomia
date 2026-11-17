using Eunomia.App.Settings;
using Eunomia.App.Updates;

namespace Eunomia.App.Tests;

/// <summary>
/// The update check (request of 16 Nov 2026): GitHub at most once a day at startup, the newest release this build may
/// move to, skip and off, and nothing recorded when GitHub cannot answer.
/// </summary>
public sealed class UpdateCheckerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The running build: a pre-release, as every build until 1.0.0.</summary>
    private static readonly ReleaseVersion Beta = ReleaseVersion.TryParse("1.0.0-beta.2")!.WithBuild(15);

    private static ReleaseInfo Release(string tag, bool prerelease = false, bool draft = false, string? url = null) =>
        new(tag, new Uri(url ?? "https://github.com/JoaoMarcos03/Eunomia/releases/tag/" + tag), prerelease, draft);

    private static (UpdateChecker Checker, FakeFeed Feed, ManualClock Clock) Create(params ReleaseInfo[] releases)
    {
        var feed = new FakeFeed(releases);
        var clock = new ManualClock(Start);
        return (new UpdateChecker(feed, clock), feed, clock);
    }

    [Fact]
    public async Task TheFirstStartAsksGitHubAndOffersTheNewestRelease()
    {
        var (checker, feed, _) = Create(
            Release("v1.0.0-beta.2-build.15", prerelease: true),
            Release("v1.0.0-beta.2-build.21", prerelease: true),
            Release("v1.0.0-beta.1-build.3", prerelease: true));
        var options = new UpdateOptions();

        var update = await checker.CheckAtStartupAsync(options, Beta, TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal("1.0.0-beta.2 (build 21)", update.Version.Display);
        Assert.Equal("v1.0.0-beta.2-build.21", update.Tag);
        Assert.Equal("https://github.com/JoaoMarcos03/Eunomia/releases/tag/v1.0.0-beta.2-build.21", update.Url.AbsoluteUri);
        Assert.Equal(1, feed.Calls);
        Assert.Equal(Start, options.LastCheck);
        Assert.Equal("v1.0.0-beta.2-build.21", options.LatestTag);
    }

    [Fact]
    public async Task WithinADayTheKeptAnswerIsUsedAndTheNoticeShowsAgain()
    {
        var (checker, feed, clock) = Create(Release("v1.0.0-beta.2-build.21", prerelease: true));
        var options = new UpdateOptions();
        var token = TestContext.Current.CancellationToken;
        await checker.CheckAtStartupAsync(options, Beta, token);
        feed.Releases = [Release("v1.0.0-beta.3-build.30", prerelease: true)];

        clock.Now = Start.AddHours(23);
        var again = await checker.CheckAtStartupAsync(options, Beta, token);

        Assert.Equal(1, feed.Calls);
        Assert.Equal("v1.0.0-beta.2-build.21", again?.Tag);

        clock.Now = Start.AddDays(1);
        var nextDay = await checker.CheckAtStartupAsync(options, Beta, token);

        Assert.Equal(2, feed.Calls);
        Assert.Equal("v1.0.0-beta.3-build.30", nextDay?.Tag);
        Assert.Equal(Start.AddDays(1), options.LastCheck);
    }

    [Fact]
    public void ItIsDueWithoutAnAnswerAfterADayOrWhenTheClockWentBack()
    {
        Assert.True(UpdateChecker.IsDue(new UpdateOptions(), Start));
        Assert.False(UpdateChecker.IsDue(new UpdateOptions { LastCheck = Start.AddHours(-23.9) }, Start));
        Assert.True(UpdateChecker.IsDue(new UpdateOptions { LastCheck = Start.AddDays(-1) }, Start));
        Assert.False(UpdateChecker.IsDue(new UpdateOptions { LastCheck = Start.AddMinutes(3) }, Start));
        Assert.True(UpdateChecker.IsDue(new UpdateOptions { LastCheck = Start.AddHours(5) }, Start));
    }

    [Fact]
    public async Task TurnedOffItDoesNotAskGitHub()
    {
        var (checker, feed, _) = Create(Release("v2.0.0-build.40"));
        var options = new UpdateOptions { CheckOnStartup = false, LatestTag = "v2.0.0-build.40", LatestUrl = Release("v2.0.0-build.40").Url.AbsoluteUri };

        var update = await checker.CheckAtStartupAsync(options, Beta, TestContext.Current.CancellationToken);

        Assert.Null(update);
        Assert.Equal(0, feed.Calls);
        Assert.Null(options.LastCheck);
    }

    [Fact]
    public async Task NothingNewerIsNoNoticeButTheAnswerIsKept()
    {
        var (checker, feed, _) = Create(Release("v1.0.0-beta.2-build.15", prerelease: true), Release("v1.0.0-beta.1-build.9", prerelease: true));
        var options = new UpdateOptions();

        var update = await checker.CheckAtStartupAsync(options, Beta, TestContext.Current.CancellationToken);

        Assert.Null(update);
        Assert.Equal(1, feed.Calls);
        Assert.Equal(Start, options.LastCheck);
        Assert.Equal("v1.0.0-beta.2-build.15", options.LatestTag);
    }

    [Fact]
    public async Task ASkippedVersionIsNotShownAtStartupUntilANewerOneComes()
    {
        var (checker, feed, clock) = Create(Release("v1.0.0-beta.2-build.21", prerelease: true));
        var options = new UpdateOptions();
        var token = TestContext.Current.CancellationToken;
        var first = await checker.CheckAtStartupAsync(options, Beta, token);
        UpdateChecker.Skip(options, first!);

        Assert.Equal("v1.0.0-beta.2-build.21", options.SkippedTag);
        Assert.Null(await checker.CheckAtStartupAsync(options, Beta, token));

        // Help › Check for updates shows it again.
        Assert.Equal("v1.0.0-beta.2-build.21", (await checker.CheckNowAsync(options, Beta, token))?.Tag);

        feed.Releases = [Release("v1.0.0-beta.2-build.22", prerelease: true)];
        clock.Now = Start.AddDays(2);
        Assert.Equal("v1.0.0-beta.2-build.22", (await checker.CheckAtStartupAsync(options, Beta, token))?.Tag);
    }

    [Fact]
    public async Task CheckNowAlwaysAsksGitHub()
    {
        var (checker, feed, _) = Create(Release("v1.0.0-beta.2-build.21", prerelease: true));
        var options = new UpdateOptions { CheckOnStartup = false, LastCheck = Start };

        var update = await checker.CheckNowAsync(options, Beta, TestContext.Current.CancellationToken);

        Assert.Equal(1, feed.Calls);
        Assert.Equal("v1.0.0-beta.2-build.21", update?.Tag);
        Assert.False(options.CheckOnStartup);
    }

    [Fact]
    public void AReleaseBuildIsNotOfferedPrereleases()
    {
        var release = ReleaseVersion.TryParse("1.0.0")!.WithBuild(30);
        ReleaseInfo[] releases =
        [
            Release("v1.1.0-beta.1-build.40", prerelease: true),
            Release("v1.2.0-build.50", prerelease: true),
            Release("v1.0.1-build.35"),
            Release("v1.0.0-build.30"),
        ];

        Assert.Equal("v1.0.1-build.35", UpdateChecker.SelectLatest(releases, release)?.Tag);
    }

    [Fact]
    public void APrereleaseBuildIsOfferedPrereleasesAndReleases()
    {
        ReleaseInfo[] releases =
        [
            Release("v1.0.0-rc.1-build.25", prerelease: true),
            Release("v1.0.0-build.30"),
            Release("v1.0.0-beta.3-build.20", prerelease: true),
        ];

        Assert.Equal("v1.0.0-build.30", UpdateChecker.SelectLatest(releases, Beta)?.Tag);
        Assert.Equal("v1.0.0-rc.1-build.25", UpdateChecker.SelectLatest(releases.Where(r => r.IsPrerelease), Beta)?.Tag);
    }

    [Fact]
    public void DraftsUntrustedPagesAndOtherTagsAreIgnored()
    {
        ReleaseInfo[] releases =
        [
            Release("v9.0.0-build.90", draft: true),
            Release("v9.0.0-build.91", url: "http://github.com/JoaoMarcos03/Eunomia/releases/tag/v9.0.0-build.91"),
            Release("v9.0.0-build.92", url: "https://github.example/JoaoMarcos03/Eunomia/releases/tag/v9.0.0-build.92"),
            Release("v9.0.0-build.93", url: "https://github.com/someone/Eunomia/releases/tag/v9.0.0-build.93"),
            Release("nightly"),
            Release("v1.0.0-beta.2-build.16", prerelease: true),
        ];

        Assert.Equal("v1.0.0-beta.2-build.16", UpdateChecker.SelectLatest(releases, Beta)?.Tag);
        Assert.Null(UpdateChecker.SelectLatest(releases.Take(5), Beta));
    }

    [Fact]
    public void ALocalBuildIsOfferedOnlyAHigherSemVer()
    {
        var local = ReleaseVersion.TryParse("1.0.0-beta.2")!;
        var sameSemVer = new UpdateOptions { LatestTag = "v1.0.0-beta.2-build.99", LatestUrl = Release("v1.0.0-beta.2-build.99").Url.AbsoluteUri };
        var higher = new UpdateOptions { LatestTag = "v1.0.0-beta.3-build.1", LatestUrl = Release("v1.0.0-beta.3-build.1").Url.AbsoluteUri };

        Assert.Null(UpdateChecker.Offer(sameSemVer, local, includeSkipped: false));
        Assert.Equal("v1.0.0-beta.3-build.1", UpdateChecker.Offer(higher, local, includeSkipped: false)?.Tag);
    }

    /// <summary>The settings file is the user's to edit: a page outside the project's releases is never offered.</summary>
    [Theory]
    [InlineData("https://example.com/JoaoMarcos03/Eunomia/releases/tag/v2.0.0")]
    [InlineData("http://github.com/JoaoMarcos03/Eunomia/releases/tag/v2.0.0")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void AKeptAnswerWithAnotherPageIsNotOffered(string? url)
    {
        var options = new UpdateOptions { LatestTag = "v2.0.0-build.40", LatestUrl = url };

        Assert.Null(UpdateChecker.Offer(options, Beta, includeSkipped: true));
    }

    [Fact]
    public void AKeptPrereleaseIsNotOfferedToAReleaseBuild()
    {
        var options = new UpdateOptions { LatestTag = "v2.0.0-rc.1-build.40", LatestUrl = Release("v2.0.0-rc.1-build.40").Url.AbsoluteUri };

        Assert.Null(UpdateChecker.Offer(options, ReleaseVersion.TryParse("1.0.0")!, includeSkipped: true));
        Assert.NotNull(UpdateChecker.Offer(options, Beta, includeSkipped: true));
    }

    [Fact]
    public async Task WhenGitHubCannotAnswerNothingIsRecorded()
    {
        var (checker, feed, _) = Create();
        feed.Error = new HttpRequestException("No such host is known.");
        var options = new UpdateOptions { LatestTag = "v1.0.0-beta.2-build.21", LatestUrl = Release("v1.0.0-beta.2-build.21").Url.AbsoluteUri };

        await Assert.ThrowsAsync<HttpRequestException>(() => checker.CheckAtStartupAsync(options, Beta, TestContext.Current.CancellationToken));

        Assert.Null(options.LastCheck);
        Assert.Equal("v1.0.0-beta.2-build.21", options.LatestTag);
        // The next start tries again, and the release found before still shows meanwhile.
        Assert.Equal("v1.0.0-beta.2-build.21", UpdateChecker.Offer(options, Beta, includeSkipped: false)?.Tag);
    }

    [Fact]
    public async Task ACancelledCheckRecordsNothing()
    {
        var (checker, _, _) = Create(Release("v1.0.0-beta.2-build.21", prerelease: true));
        var options = new UpdateOptions();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAtStartupAsync(options, Beta, cancel.Token));

        Assert.Null(options.LastCheck);
    }

    [Theory]
    [InlineData("https://github.com/JoaoMarcos03/Eunomia/releases/tag/v1.0.0-beta.2-build.15", true)]
    [InlineData("https://GitHub.com/joaomarcos03/eunomia/releases/tag/v1", true)]
    [InlineData("https://github.com/JoaoMarcos03/Eunomia/releases", false)]
    [InlineData("https://github.com/JoaoMarcos03/Eunomia/releasesX/tag/v1", false)]
    [InlineData("https://github.com/JoaoMarcos03/Eunomia/releases/../../evil/releases/tag/v1", false)]
    [InlineData("http://github.com/JoaoMarcos03/Eunomia/releases/tag/v1", false)]
    [InlineData("https://github.com:8443/JoaoMarcos03/Eunomia/releases/tag/v1", false)]
    [InlineData("https://github.com.evil.example/JoaoMarcos03/Eunomia/releases/tag/v1", false)]
    [InlineData("https://user@github.com/JoaoMarcos03/Eunomia/releases/tag/v1", false)]
    public void OnlyTheProjectsReleasePagesAreTrusted(string url, bool trusted) =>
        Assert.Equal(trusted, ReleasePages.IsTrusted(new Uri(url)));

    private sealed class FakeFeed(IReadOnlyList<ReleaseInfo> releases) : IReleaseFeed
    {
        public IReadOnlyList<ReleaseInfo> Releases { get; set; } = releases;

        public Exception? Error { get; set; }

        public int Calls { get; private set; }

        public Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Error is { } error ? Task.FromException<IReadOnlyList<ReleaseInfo>>(error) : Task.FromResult(Releases);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
