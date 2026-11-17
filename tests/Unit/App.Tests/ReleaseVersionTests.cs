using Eunomia.App.Updates;

namespace Eunomia.App.Tests;

/// <summary>The versions in the GitHub release tags (update check, request of 16 Nov 2026).</summary>
public sealed class ReleaseVersionTests
{
    private static ReleaseVersion V(string text) => ReleaseVersion.TryParse(text) ?? throw new InvalidOperationException("Not a version: " + text);

    [Fact]
    public void ReadsTheTagsTheCiPublishes()
    {
        var version = V("v1.0.0-beta.2-build.15");

        Assert.Equal((1, 0, 0), (version.Major, version.Minor, version.Patch));
        Assert.Equal("beta.2", version.Prerelease);
        Assert.True(version.IsPrerelease);
        Assert.Equal(15, version.Build);
        Assert.Equal("1.0.0-beta.2", version.SemVer);
        Assert.Equal("1.0.0-beta.2 (build 15)", version.Display);
        Assert.Equal("1.0.0-beta.2-build.15", version.ToString());
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "", null)]
    [InlineData("V2.0.0", 2, 0, 0, "", null)]
    [InlineData(" v1.0.0 ", 1, 0, 0, "", null)]
    [InlineData("1.0.0-build.7", 1, 0, 0, "", 7)]
    [InlineData("1.0.0-rc.1-build.3", 1, 0, 0, "rc.1", 3)]
    [InlineData("1.0.0-beta.2+5f3ddb37", 1, 0, 0, "beta.2", null)]
    [InlineData("v1.0.0-beta.2-build.15+sha.abc", 1, 0, 0, "beta.2", 15)]
    [InlineData("10.20.30-alpha-1.x", 10, 20, 30, "alpha-1.x", null)]
    public void ReadsVersions(string text, int major, int minor, int patch, string prerelease, int? build)
    {
        var version = V(text);

        Assert.Equal((major, minor, patch, prerelease, build), (version.Major, version.Minor, version.Patch, version.Prerelease, version.Build));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nightly")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.x")]
    [InlineData("-1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("1.0.0-be ta")]
    [InlineData("1.0.0-build.x")]
    [InlineData("1.0.0-build.-1")]
    [InlineData("1.0.0-build.")]
    [InlineData("99999999999.0.0")]
    public void RefusesWhatIsNotAVersion(string? text) => Assert.Null(ReleaseVersion.TryParse(text));

    /// <summary>SemVer 2.0 §11 (its own example), then the build number between builds of the same SemVer.</summary>
    [Fact]
    public void OrdersBySemVerThenBuild()
    {
        string[] ascending =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
            "1.0.0-beta.2-build.3", "1.0.0-beta.2-build.15", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.0-build.40",
            "1.0.1", "1.1.0", "2.0.0",
        ];

        for (var i = 1; i < ascending.Length; i++)
        {
            var lower = V(ascending[i - 1]);
            var higher = V(ascending[i]);
            Assert.True(higher > lower, $"{higher} > {lower}");
            Assert.True(lower < higher, $"{lower} < {higher}");
            Assert.True(higher.CompareTo(lower) > 0);
        }
        var shuffled = ascending.Reverse().Select(V).ToList();
        shuffled.Sort();
        Assert.Equal(ascending, shuffled.Select(v => v.ToString()));
    }

    [Fact]
    public void ComparesPrereleaseIdentifiersIgnoringCase()
    {
        Assert.Equal(V("1.0.0-BETA.2"), V("v1.0.0-beta.2"));
        Assert.Equal(V("1.0.0-BETA.2").GetHashCode(), V("v1.0.0-beta.2").GetHashCode());
        Assert.True(V("1.0.0-beta.2") == V("1.0.0-Beta.2"));
        Assert.True(V("1.0.0-beta.2-build.1") != V("1.0.0-beta.2-build.2"));
        Assert.False(V("1.0.0").Equals(null));
        Assert.True(V("1.0.0").CompareTo(null) > 0);
    }

    [Fact]
    public void NewerMeansAHigherSemVerOrAHigherKnownBuild()
    {
        var running = V("1.0.0-beta.2").WithBuild(15);

        Assert.True(V("v1.0.0-beta.2-build.20").IsNewerThan(running));
        Assert.True(V("v1.0.0-beta.3-build.1").IsNewerThan(running));
        Assert.True(V("v1.0.0-build.16").IsNewerThan(running));
        Assert.False(V("v1.0.0-beta.2-build.15").IsNewerThan(running));
        Assert.False(V("v1.0.0-beta.2-build.9").IsNewerThan(running));
        Assert.False(V("v1.0.0-beta.1-build.99").IsNewerThan(running));
        Assert.False(V("v1.0.0-beta.2").IsNewerThan(running));
    }

    [Fact]
    public void ALocalBuildIsNotOlderThanAReleaseOfTheSameSemVer()
    {
        var local = V("1.0.0-beta.2");

        Assert.False(V("v1.0.0-beta.2-build.99").IsNewerThan(local));
        Assert.True(V("v1.0.0-beta.3-build.1").IsNewerThan(local));
    }

    [Fact]
    public void WithBuildKeepsTheSemVer()
    {
        var version = V("1.0.0-beta.2").WithBuild(15);

        Assert.Equal("1.0.0-beta.2 (build 15)", version.Display);
        Assert.Equal("1.0.0-beta.2", version.WithBuild(null).Display);
    }
}
