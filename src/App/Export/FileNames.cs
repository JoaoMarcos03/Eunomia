using System.Buffers;
using System.Text;

namespace Eunomia.App.Export;

/// <summary>
/// File names made from names that come from the server or the user (tables, databases, sessions, columns, snippets).
/// Always Windows rules, on every system: a file written on Linux or macOS is often copied to Windows, and
/// <see cref="Path.GetInvalidFileNameChars"/> there only refuses <c>/</c> and NUL.
/// </summary>
public static class FileNames
{
    private static readonly SearchValues<char> Invalid = SearchValues.Create("\"<>|:*?\\/");

    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL"];

    /// <summary>
    /// A valid file name: the characters Windows refuses (and control characters) become <c>_</c>; trailing dots and
    /// spaces go (Windows drops them, so two names would meet); a device name (<c>CON</c>, <c>NUL</c>, <c>COM1</c>…,
    /// with or without an extension) gets <c>_</c> after it. Empty, <c>.</c> and <c>..</c> become <paramref name="empty"/>.
    /// </summary>
    public static string Safe(string name, string empty = "_")
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentException.ThrowIfNullOrEmpty(empty);
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(c < ' ' || Invalid.Contains(c) ? '_' : c);
        }
        var result = sb.ToString().TrimEnd('.', ' ');
        if (result.Length == 0)
        {
            return empty;
        }
        // Windows also ignores spaces before the extension: "con .txt" is the console too.
        var dot = result.IndexOf('.', StringComparison.Ordinal);
        var device = (dot < 0 ? result : result[..dot]).TrimEnd(' ');
        return IsDevice(device) ? device + "_" + result[device.Length..] : result;
    }

    private static bool IsDevice(string stem) =>
        Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)
        || (stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is (>= '1' and <= '9') or '¹' or '²' or '³');
}
