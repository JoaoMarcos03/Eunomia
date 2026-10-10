using System.Globalization;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Data;

/// <summary>
/// Values of the grid's Insert value ▸ (appendix D B16): datetime, date, time and year from the server clock; unix timestamp
/// from the local clock (like HeidiSQL, the local time read as UTC) and the UTC version; datetime, date and time in UTC;
/// four GUID forms. The caption shows the value that goes into the cell ("datetime: 2026-10-06 18:30:00").
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
    /// "Default value: x" only for literal defaults (quoted text or a number), not for expressions such as
    /// <c>CURRENT_TIMESTAMP</c>; <c>null</c> if the column has none.
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
