using System.Globalization;
using System.Text;

namespace Eunomia.Infra.Settings.HeidiImport;

/// <summary>A value of the HeidiSQL settings file: key path, name, type (1 = text, 3 = integer) and value.</summary>
public sealed record HeidiValue(IReadOnlyList<string> KeyPath, string Name, int Type, string Value)
{
    public int? AsInt() => int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public bool AsBool() => Value is "1" || Value.Equals("true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Tolerant reading of the settings file exported by HeidiSQL (appendix A §1.8, REQ-SES-02): one line per
/// value in the format <c>&lt;path&gt;\&lt;Name&gt;&lt;|||&gt;&lt;type&gt;&lt;|||&gt;&lt;value&gt;</c>, with CR and LF inside values
/// encoded as <c>&lt;{{{&gt;</c> and <c>&lt;}}}&gt;</c>. Invalid lines are counted and ignored.
/// </summary>
public sealed class HeidiSettingsFile
{
    public const string Delimiter = "<|||>";
    public const string EncodedCr = "<{{{>";
    public const string EncodedLf = "<}}}>";

    public List<HeidiValue> Values { get; } = [];

    public int InvalidLines { get; private set; }

    public static HeidiSettingsFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var file = new HeidiSettingsFile();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            var parts = line.Split(Delimiter);
            if (parts.Length != 3 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
            {
                file.InvalidLines++;
                continue;
            }
            var path = parts[0].Split('\\');
            var value = parts[2].Replace(EncodedCr, "\r", StringComparison.Ordinal).Replace(EncodedLf, "\n", StringComparison.Ordinal);
            file.Values.Add(new HeidiValue(path[..^1], path[^1], type, value));
        }
        return file;
    }

    public static HeidiSettingsFile Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));
}

/// <summary>
/// HeidiSQL passwords (appendix A §7): reversible obfuscation, a "salt" from 1 to 9 added to each character and written
/// in hex, with the salt digit at the end. ANSI variant: 2 hex digits per character, a sum above 255 wraps around by
/// subtracting 255. Unicode variant (some character above 255): 4 digits per character, modulo 65536, and a trailing
/// <c>0</c> marking the variant.
/// </summary>
public static class HeidiPassword
{
    public static string Decrypt(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return string.Empty;
        }
        // Unicode: 4n digits + salt + "0" (even length); ANSI: 2n digits + salt (odd). A value that gives 0
        // after removing the salt can only come from a character that wrapped around (ÿ + salt - 255 = salt).
        var unicode = encoded.Length >= 2 && encoded.Length % 2 == 0 && encoded[^1] == '0' && (encoded.Length - 2) % 4 == 0;
        var saltChar = unicode ? encoded[^2] : encoded[^1];
        if (!char.IsAsciiDigit(saltChar))
        {
            throw new FormatException("Invalid HeidiSQL password.");
        }
        var salt = saltChar - '0';
        var body = encoded[..^(unicode ? 2 : 1)];
        var width = unicode ? 4 : 2;
        if (body.Length % width != 0)
        {
            throw new FormatException("Invalid HeidiSQL password.");
        }
        var sb = new StringBuilder(body.Length / width);
        for (var i = 0; i < body.Length; i += width)
        {
            var value = int.Parse(body.AsSpan(i, width), NumberStyles.HexNumber, CultureInfo.InvariantCulture) - salt;
            if (value <= 0)
            {
                value += unicode ? 65536 : 255;
            }
            sb.Append((char)value);
        }
        return sb.ToString();
    }

    /// <summary>The inverse of <see cref="Decrypt"/> (for tests and for the compatible export).</summary>
    public static string Encrypt(string password, int salt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(salt, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(salt, 9);
        var unicode = password.Any(c => c > 255);
        var sb = new StringBuilder();
        foreach (var c in password)
        {
            var value = c + salt;
            if (unicode)
            {
                sb.Append((value % 65536).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append((value > 255 ? value - 255 : value).ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        sb.Append(salt.ToString(CultureInfo.InvariantCulture));
        if (unicode)
        {
            sb.Append('0');
        }
        return sb.ToString();
    }
}
