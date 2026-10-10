using System.Globalization;
using System.Text;

namespace Eunomia.Infra.Settings.HeidiImport;

/// <summary>Um valor do ficheiro de settings do HeidiSQL: caminho da chave, nome, tipo (1 = texto, 3 = inteiro) e valor.</summary>
public sealed record HeidiValue(IReadOnlyList<string> KeyPath, string Name, int Type, string Value)
{
    public int? AsInt() => int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public bool AsBool() => Value is "1" || Value.Equals("true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Leitura tolerante do ficheiro de settings exportado pelo HeidiSQL (anexo A §1.8, REQ-SES-02): uma linha por
/// valor no formato <c>&lt;caminho&gt;\&lt;Nome&gt;&lt;|||&gt;&lt;tipo&gt;&lt;|||&gt;&lt;valor&gt;</c>, CR e LF dentro dos valores
/// codificados como <c>&lt;{{{&gt;</c> e <c>&lt;}}}&gt;</c>. Linhas inválidas são contadas e ignoradas.
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
/// Passwords do HeidiSQL (anexo A §7): ofuscação reversível, um "sal" de 1 a 9 somado a cada carácter e escrito em
/// hex, com o dígito do sal no fim. Variante ANSI: 2 dígitos hex por carácter, soma acima de 255 dá a volta
/// subtraindo 255. Variante Unicode (algum carácter acima de 255): 4 dígitos por carácter, módulo 65536, e um
/// <c>0</c> final a marcar a variante.
/// </summary>
public static class HeidiPassword
{
    public static string Decrypt(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return string.Empty;
        }
        // Unicode: 4n dígitos + sal + "0" (comprimento par); ANSI: 2n dígitos + sal (ímpar). Um valor que dá 0
        // depois de tirar o sal só pode vir de um carácter que deu a volta (ÿ + sal - 255 = sal).
        var unicode = encoded.Length >= 2 && encoded.Length % 2 == 0 && encoded[^1] == '0' && (encoded.Length - 2) % 4 == 0;
        var saltChar = unicode ? encoded[^2] : encoded[^1];
        if (!char.IsAsciiDigit(saltChar))
        {
            throw new FormatException("Password do HeidiSQL inválida.");
        }
        var salt = saltChar - '0';
        var body = encoded[..^(unicode ? 2 : 1)];
        var width = unicode ? 4 : 2;
        if (body.Length % width != 0)
        {
            throw new FormatException("Password do HeidiSQL inválida.");
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

    /// <summary>Inverso de <see cref="Decrypt"/> (para testes e para o export compatível).</summary>
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
