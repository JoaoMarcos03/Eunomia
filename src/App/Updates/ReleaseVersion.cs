using System.Globalization;

namespace Eunomia.App.Updates;

/// <summary>
/// A version of Eunomia as the GitHub releases name it: <c>v1.0.0-beta.2-build.15</c>, the SemVer of
/// <c>Directory.Build.props</c> and the number of the CI run that built it (every push to main publishes one). The
/// SemVer decides first (2.0 › 1.1 › 1.1-rc.1 › 1.1-beta.2); between builds of the same SemVer, the higher run number.
/// A build of unknown number (a local build) is never older than a release of the same SemVer.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private const string BuildSuffix = "-build.";

    private readonly string[] _prerelease;

    private ReleaseVersion(int major, int minor, int patch, string[] prerelease, int? build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
        Build = build;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The SemVer pre-release identifiers ("beta.2"), empty for a release.</summary>
    public string Prerelease => string.Join('.', _prerelease);

    public bool IsPrerelease => _prerelease.Length > 0;

    /// <summary>The CI run that built it; <c>null</c> when unknown.</summary>
    public int? Build { get; }

    /// <summary>The SemVer without the build ("1.0.0-beta.2"), as the user sees it.</summary>
    public string SemVer => IsPrerelease
        ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Prerelease}")
        : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>"1.0.0-beta.2 (build 15)" or "1.0.0-beta.2".</summary>
    public string Display => Build is { } build
        ? string.Create(CultureInfo.InvariantCulture, $"{SemVer} (build {build})")
        : SemVer;

    /// <summary>The same SemVer with a build number (the running build's, which the tag does not carry).</summary>
    public ReleaseVersion WithBuild(int? build) => new(Major, Minor, Patch, _prerelease, build);

    /// <summary>
    /// Reads a tag or a version: an optional "v", <c>major.minor.patch</c>, an optional "-pre.release", an optional
    /// "-build.N" and an optional "+metadata" (ignored). <c>null</c> for anything else.
    /// </summary>
    public static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }
        if (s.IndexOf('+', StringComparison.Ordinal) is var plus and >= 0)
        {
            s = s[..plus];
        }
        int? build = null;
        if (s.LastIndexOf(BuildSuffix, StringComparison.OrdinalIgnoreCase) is var b and > 0)
        {
            if (!TryParseNumber(s[(b + BuildSuffix.Length)..], out var number))
            {
                return null;
            }
            build = number;
            s = s[..b];
        }
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        var core = dash < 0 ? s : s[..dash];
        string[] prerelease = [];
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..].Split('.');
            if (prerelease.Any(p => !IsIdentifier(p)))
            {
                return null;
            }
        }
        var parts = core.Split('.');
        return parts.Length == 3
               && TryParseNumber(parts[0], out var major)
               && TryParseNumber(parts[1], out var minor)
               && TryParseNumber(parts[2], out var patch)
            ? new ReleaseVersion(major, minor, patch, prerelease, build)
            : null;
    }

    /// <summary>Newer than <paramref name="current"/>: a higher SemVer, or the same with a higher known build.</summary>
    public bool IsNewerThan(ReleaseVersion current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var semVer = CompareSemVer(this, current);
        return semVer > 0 || (semVer == 0 && Build is { } build && current.Build is { } running && build > running);
    }

    /// <summary>SemVer precedence, then the build (unknown first).</summary>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        var semVer = CompareSemVer(this, other);
        return semVer != 0 ? semVer : Nullable.Compare(Build, other.Build);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as ReleaseVersion);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease.ToUpperInvariant(), Build);

    public override string ToString() => Build is { } build
        ? string.Create(CultureInfo.InvariantCulture, $"{SemVer}{BuildSuffix}{build}")
        : SemVer;

    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right) => !(left == right);

    public static bool operator <(ReleaseVersion? left, ReleaseVersion? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(ReleaseVersion? left, ReleaseVersion? right) => left is null || left.CompareTo(right) <= 0;

    public static bool operator >(ReleaseVersion? left, ReleaseVersion? right) => left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(ReleaseVersion? left, ReleaseVersion? right) => left is null ? right is null : left.CompareTo(right) >= 0;

    /// <summary>SemVer 2.0 §11: numbers, then a release above its pre-releases, then identifier by identifier.</summary>
    private static int CompareSemVer(ReleaseVersion a, ReleaseVersion b)
    {
        var c = a.Major.CompareTo(b.Major);
        if (c == 0)
        {
            c = a.Minor.CompareTo(b.Minor);
        }
        if (c == 0)
        {
            c = a.Patch.CompareTo(b.Patch);
        }
        if (c != 0)
        {
            return c;
        }
        if (a.IsPrerelease != b.IsPrerelease)
        {
            return a.IsPrerelease ? -1 : 1;
        }
        for (var i = 0; i < Math.Min(a._prerelease.Length, b._prerelease.Length); i++)
        {
            c = CompareIdentifier(a._prerelease[i], b._prerelease[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return a._prerelease.Length.CompareTo(b._prerelease.Length);
    }

    /// <summary>Numeric identifiers compare as numbers and below alphanumeric ones; those compare in ASCII order (case-insensitive here).</summary>
    private static int CompareIdentifier(string a, string b)
    {
        var aNumber = TryParseNumber(a, out var x);
        var bNumber = TryParseNumber(b, out var y);
        return (aNumber, bNumber) switch
        {
            (true, true) => x.CompareTo(y),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static bool TryParseNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool IsIdentifier(string text) =>
        text.Length > 0 && text.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');
}
