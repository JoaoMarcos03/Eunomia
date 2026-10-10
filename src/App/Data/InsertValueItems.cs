using System.Globalization;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Data;

/// <summary>
/// Valores do Insert value ▸ do grid (anexo D B16): datetime, date, time e year do relógio do servidor; unix timestamp do
/// relógio local (como o HeidiSQL, a hora local lida como UTC) e a versão UTC; datetime, date e time em UTC; quatro
/// formas de GUID. A caption mostra o valor que vai para a célula ("datetime: 2026-10-06 18:30:00").
/// </summary>
public static class InsertValueItems
{
    public static IReadOnlyDictionary<string, string> Compute(DateTime serverNow, DateTime localNow, DateTime utcNow, Guid guid)
    {
        var c = CultureInfo.InvariantCulture;
        var braces = guid.ToString("B", c).ToUpperInvariant();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["insertValue.datetime"] = serverNow.ToString("yyyy-MM-dd HH:mm:ss", c),
            ["insertValue.date"] = serverNow.ToString("yyyy-MM-dd", c),
            ["insertValue.time"] = serverNow.ToString("HH:mm:ss", c),
            ["insertValue.year"] = serverNow.ToString("yyyy", c),
            ["insertValue.unixTimestamp"] = new DateTimeOffset(DateTime.SpecifyKind(localNow, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(c),
            ["insertValue.utcDatetime"] = utcNow.ToString("yyyy-MM-dd HH:mm:ss", c),
            ["insertValue.utcDate"] = utcNow.ToString("yyyy-MM-dd", c),
            ["insertValue.utcTime"] = utcNow.ToString("HH:mm:ss", c),
            ["insertValue.utcUnixTimestamp"] = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(c),
            ["insertValue.guid"] = braces,
            ["insertValue.guidNoBraces"] = braces[1..^1],
            ["insertValue.guidLower"] = braces.ToLowerInvariant(),
            ["insertValue.guidLowerNoBraces"] = braces[1..^1].ToLowerInvariant(),
        };
    }

    /// <summary>
    /// "Default value: x" só para defaults literais (texto entre aspas ou número), não para expressões como
    /// <c>CURRENT_TIMESTAMP</c>; <c>null</c> se a coluna não tiver um.
    /// </summary>
    public static string? DefaultLiteral(ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var d = column.DefaultExpression?.Trim();
        if (string.IsNullOrEmpty(d))
        {
            return null;
        }
        if (d.Length >= 2 && d[0] == '\'' && d[^1] == '\'')
        {
            return d[1..^1].Replace("''", "'", StringComparison.Ordinal).Replace("\\'", "'", StringComparison.Ordinal);
        }
        return decimal.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? d : null;
    }
}
