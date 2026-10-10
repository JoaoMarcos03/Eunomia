using System.Globalization;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Results;

namespace Eunomia.App.Objects;

/// <summary>
/// What the table editor needs from the server (appendix C §5): the lists (collations, engines, tables of the database)
/// and saving (CREATE for a new table, ALTER by diff for an existing one), on the primary connection so the SQL goes to the log.
/// </summary>
public static class TableEditing
{
    /// <summary>Collations (all of them, sorted), available engines and the tables of the database.</summary>
    public static async Task<(IReadOnlyList<string> Collations, IReadOnlyList<string> Engines, IReadOnlyList<string> Tables)> ListsAsync(
        OpenSession session, string database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Collations and engines do not change during the session: one round trip for the first table, not for every one.
        var collations = await session.Metadata.GetServerListAsync("collations", (h, ct) => h.Metadata.GetCollationsAsync(ct), cancellationToken).ConfigureAwait(false);
        var engines = await session.Metadata.GetServerListAsync("engines", (h, ct) => QueryRows.ReadAsync(h, "SHOW ENGINES", ct), cancellationToken).ConfigureAwait(false);
        var objects = await session.Metadata.GetObjectsAsync(database, cancellationToken).ConfigureAwait(false);
        return (
            [.. collations.Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase)],
            [.. engines.Where(r => r.Length > 1 && !string.Equals(r[1], "NO", StringComparison.OrdinalIgnoreCase) && !string.Equals(r[1], "DISABLED", StringComparison.OrdinalIgnoreCase)).Select(r => r[0] ?? string.Empty)],
            [.. objects.Objects.Where(o => o.Ref.Kind == DbObjectKind.Table).Select(o => o.Ref.Name)]);
    }

    /// <summary>Statements to save: CREATE (without <paramref name="baseline"/>) or ALTER by diff.</summary>
    public static IReadOnlyList<string> Statements(IServerServices services, TableDefinition? baseline, TableDesign design)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(design);
        var wanted = design.ToDefinition(services.Types, services.Dialect);
        return baseline is null ? services.Ddl.Create(wanted) : services.Ddl.Alter(baseline, wanted);
    }

    /// <summary>
    /// Changes that may lose data, to confirm before saving: dropped columns and columns that change type or
    /// charset (the strict mode of <see cref="ApplyAsync"/> makes truncations fail, but not rounding such as DOUBLE →
    /// FLOAT or fewer decimal places).
    /// </summary>
    public static IReadOnlyList<string> DataLossWarnings(TableDefinition? baseline, TableDefinition wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        if (baseline is null)
        {
            return [];
        }
        var warnings = new List<string>();
        var kept = wanted.Columns.Where(c => c.OriginalName is not null).ToDictionary(c => c.OriginalName!, StringComparer.OrdinalIgnoreCase);
        foreach (var old in baseline.Columns)
        {
            if (!kept.TryGetValue(old.Name, out var now))
            {
                warnings.Add($"Drop column \"{old.Name}\" and all its values");
                continue;
            }
            if (!string.Equals(Normalize(old.NativeType), Normalize(now.NativeType), StringComparison.OrdinalIgnoreCase) && !IsWidening(old.NativeType, now.NativeType))
            {
                warnings.Add($"Change column \"{old.Name}\" from {old.NativeType} to {now.NativeType}");
            }
            else if (Charset(old.Type.Collation) is { } before && Charset(now.Type.Collation) is { } after && !string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Convert column \"{old.Name}\" from {before} to {after}");
            }
        }
        if (wanted.Options.TryGetValue("ConvertCollation", out var convert) && convert.Length > 0)
        {
            warnings.Add($"Convert all text columns to {convert}");
        }
        return warnings;

        static string Normalize(string type) => type.Replace(" ", string.Empty, StringComparison.Ordinal);

        static string? Charset(string? collation) => string.IsNullOrEmpty(collation) ? null : collation.Split('_')[0];
    }

    /// <summary>Families in which each type fits into the next (same signedness).</summary>
    private static readonly string[][] Widening =
    [
        ["tinyint", "smallint", "mediumint", "int", "integer", "bigint"],
        ["float", "double", "real"],
        ["char", "varchar", "tinytext", "text", "mediumtext", "longtext"],
        ["binary", "varbinary", "tinyblob", "blob", "mediumblob", "longblob"],
    ];

    /// <summary>
    /// The type change loses no values: the same type with a larger length, precision, scale or fraction, or a larger
    /// type of the same family with the same signedness (<c>int</c> → <c>bigint</c>, <c>varchar(20)</c> → <c>text</c>).
    /// </summary>
    internal static bool IsWidening(string before, string after)
    {
        var (oldName, oldArgs, oldRest) = Parse(before);
        var (newName, newArgs, newRest) = Parse(after);
        if (!string.Equals(oldRest, newRest, StringComparison.Ordinal))
        {
            return false;
        }
        if (oldName == newName)
        {
            if (oldName is "enum" or "set")
            {
                return false;
            }
            if (oldArgs.Count == 0 && newArgs.Count == 1 && oldName is "datetime" or "timestamp" or "time")
            {
                return true;
            }
            if (oldArgs.Count == 0 || newArgs.Count == 0)
            {
                // int(11) → int: the display width does not count; the rest without arguments is the same type.
                return oldName.EndsWith("int", StringComparison.Ordinal) || (oldArgs.Count == 0 && newArgs.Count == 0);
            }
            if (oldArgs.Count != newArgs.Count)
            {
                return false;
            }
            if (oldName is "decimal" or "numeric" && oldArgs.Count == 2)
            {
                // Both the integer and the decimal digits have to fit.
                return newArgs[1] >= oldArgs[1] && newArgs[0] - newArgs[1] >= oldArgs[0] - oldArgs[1];
            }
            return oldArgs.Zip(newArgs).All(p => p.Second >= p.First) || oldName.EndsWith("int", StringComparison.Ordinal);
        }
        foreach (var family in Widening)
        {
            var from = Array.IndexOf(family, oldName);
            var to = Array.IndexOf(family, newName);
            if (from >= 0 && to > from)
            {
                // char(n)/varchar(n) → text: fits if n fits in the text type's bytes (utf8mb4: 4 bytes per character).
                return oldName is not ("char" or "varchar" or "binary" or "varbinary") || newName is "varchar" or "varbinary"
                    ? (newArgs.Count == 0 || oldArgs.Count == 0 || newArgs[0] >= oldArgs[0])
                    : (oldArgs.Count == 1 ? oldArgs[0] * 4 : 255) <= Capacity(newName);
            }
        }
        return false;

        static long Capacity(string type) => type switch
        {
            "tinytext" or "tinyblob" => 255,
            "text" or "blob" => 65_535,
            "mediumtext" or "mediumblob" => 16_777_215,
            _ => 4_294_967_295,
        };

        static (string Name, List<long> Args, string Tail) Parse(string type)
        {
            var t = type.Trim().ToLowerInvariant();
            var open = t.IndexOf('(', StringComparison.Ordinal);
            var close = t.IndexOf(')', StringComparison.Ordinal);
            var space = t.IndexOf(' ', StringComparison.Ordinal);
            var name = open > 0 ? t[..open] : space > 0 ? t[..space] : t;
            var args = new List<long>();
            if (open > 0 && close > open)
            {
                foreach (var part in t[(open + 1)..close].Split(','))
                {
                    if (long.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    {
                        args.Add(n);
                    }
                    else
                    {
                        args.Clear();
                        args.Add(-1);
                        break;
                    }
                }
            }
            var rest = (close > 0 ? t[(close + 1)..] : space > 0 ? t[space..] : string.Empty).Trim();
            return (name, args, rest);
        }
    }

    /// <summary>
    /// ALTER that restores the foreign keys that the first ALTER of <see cref="IDdlComposer.Alter"/> drops (changed or
    /// deleted), in case the main ALTER fails afterwards; <c>null</c> if none is dropped.
    /// </summary>
    public static string? RestoreDroppedForeignKeys(IServerServices services, TableDefinition? baseline, TableDesign design)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(design);
        if (baseline is null)
        {
            return null;
        }
        var wanted = design.ToDefinition(services.Types, services.Dialect);
        var after = wanted.ForeignKeys.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var dropped = baseline.ForeignKeys.Where(f => !after.TryGetValue(f.Name, out var now) || services.Ddl.ForeignKeySpec(now) != services.Ddl.ForeignKeySpec(f)).ToList();
        return dropped.Count == 0
            ? null
            : $"ALTER TABLE {services.Dialect.QualifiedName(baseline.Ref)}\n\t" + string.Join(",\n\t", dropped.Select(f => "ADD " + services.Ddl.ForeignKeySpec(f)));
    }

    /// <summary>
    /// Runs the statements in order on the primary connection (<see cref="SafeDdl"/>, in strict mode: an ALTER that would
    /// truncate values fails; the previous mode is always restored). The first one that fails stops and throws; if statements
    /// had already run and there is a <paramref name="restoreForeignKeys"/>, it runs before throwing.
    /// </summary>
    public static async Task ApplyAsync(OpenSession session, IReadOnlyList<string> statements, CancellationToken cancellationToken, string? restoreForeignKeys = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(statements);
        await SafeDdl.RunAsync(session.Host, strict: true, async (h, ct) =>
        {
            var done = 0;
            try
            {
                foreach (var sql in statements)
                {
                    await h.ExecuteNonQueryAsync(sql, ct).ConfigureAwait(false);
                    done++;
                }
            }
            catch (Exception) when (done > 0 && restoreForeignKeys is not null && !ct.IsCancellationRequested && h.IsOpen)
            {
                // The first ALTER dropped foreign keys and the next one failed: the table is left as it was.
                await h.ExecuteNonQueryAsync(restoreForeignKeys, ct).ConfigureAwait(false);
                throw;
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rows of a small query (raw text, NULL = <c>null</c>), on the metadata connection.</summary>
    public static Task<List<string?[]>> RowsAsync(SessionHost host, string sql, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.RunMetadataAsync((h, ct) => QueryRows.ReadAsync(h, sql, ct), cancellationToken);
    }
}
