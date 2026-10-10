using System.Globalization;
using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Objects;

/// <summary>
/// Copy table request: target database and name, the chosen columns, indexes and foreign keys, whether the data is
/// copied and the data's WHERE filter (appendix B §5.1).
/// </summary>
public sealed record TableCopyRequest(
    string TargetDatabase,
    string TargetName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<string> ForeignKeys,
    bool Data,
    string? Where);

/// <summary>
/// Copy table (Create new ▸ Table copy; appendix B §5.1): a <c>CREATE TABLE</c> from the composer with the chosen elements
/// and, with the data, an <c>INSERT … SELECT</c> that runs entirely on the server. The target is never a table that
/// already exists: the <c>CREATE TABLE</c> has no <c>IF NOT EXISTS</c> (the server refuses and the INSERT does not run)
/// and the user is warned beforehand (HeidiSQL offered to drop the target; here that is a separate Drop).
/// </summary>
public static class TableCopy
{
    /// <summary>Maximum length of an identifier (MySQL and MariaDB).</summary>
    private const int MaxIdentifier = 64;

    /// <summary>Default target name.</summary>
    public static string DefaultName(string source) => source + "_copy";

    /// <summary>
    /// Target structure: only the chosen columns; the chosen indexes and foreign keys whose columns were all copied; checks
    /// only when every column was copied (the expression may use any column). An AUTO_INCREMENT column without a copied
    /// index starting with it loses the AUTO_INCREMENT (otherwise the server refuses the table, ER_WRONG_AUTO_KEY). In the
    /// same database, foreign keys and checks get new names derived from the target, because the names of these
    /// constraints are unique per database.
    /// </summary>
    public static TableDefinition TargetDefinition(TableDefinition source, TableCopyRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TargetName))
        {
            throw new InvalidOperationException("Enter a name for the new table.");
        }
        var chosen = request.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = source.Columns.Where(c => chosen.Contains(c.Name)).ToList();
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("Select at least one column.");
        }
        var copied = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexNames = request.Indexes.ToHashSet(StringComparer.Ordinal);
        var indexes = source.Indexes.Where(i => indexNames.Contains(i.Name) && i.Columns.All(copied.Contains)).ToList();
        columns = [.. columns.Select(c => c.IsAutoIncrement && !indexes.Any(i => i.Columns.Count > 0 && string.Equals(i.Columns[0], c.Name, StringComparison.OrdinalIgnoreCase))
            ? c with { IsAutoIncrement = false }
            : c)];
        var sameDatabase = string.Equals(source.Ref.Schema, request.TargetDatabase, StringComparison.Ordinal);
        var fkNames = request.ForeignKeys.ToHashSet(StringComparer.Ordinal);
        var foreignKeys = source.ForeignKeys.Where(f => fkNames.Contains(f.Name) && f.Columns.All(copied.Contains))
            .Select((f, i) => sameDatabase ? f with { Name = ConstraintName(request.TargetName, "ibfk", i + 1) } : f)
            .ToList();
        var checks = copied.Count == source.Columns.Count
            ? source.Checks.Select((c, i) => sameDatabase ? c with { Name = ConstraintName(request.TargetName, "chk", i + 1) } : c).ToList()
            : [];
        var target = new DbObjectRef(request.TargetDatabase, request.TargetDatabase, request.TargetName.Trim(), DbObjectKind.Table);
        return new TableDefinition(target, columns, indexes, foreignKeys, checks, source.Options);
    }

    /// <summary>
    /// <c>INSERT INTO target (columns) SELECT columns FROM source [WHERE filter]</c>, without the generated columns (the
    /// server computes them); <c>null</c> if there are no columns to copy. The filter follows the Data tab filter rules (a
    /// single condition, no <c>;</c> in the middle).
    /// </summary>
    public static string? InsertStatement(IDialect dialect, DbObjectRef source, TableDefinition target, string? where)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var columns = target.Columns.Where(c => !c.IsGenerated).Select(c => dialect.QuoteIdentifier(c.Name)).ToList();
        if (columns.Count == 0)
        {
            return null;
        }
        var list = string.Join(", ", columns);
        var sql = $"INSERT INTO {dialect.QualifiedName(target.Ref)} ({list}) SELECT {list} FROM {dialect.QualifiedName(source)}";
        return DataTabQuery.WhereClause(where) is { } filter ? sql + " WHERE " + filter : sql;
    }

    /// <summary>Copy table statements, in order: the CREATE and, with the data, the INSERT … SELECT.</summary>
    public static IReadOnlyList<string> Statements(IServerServices services, TableDefinition source, TableCopyRequest request)
    {
        ArgumentNullException.ThrowIfNull(services);
        var target = TargetDefinition(source, request);
        var statements = services.Ddl.Create(target).ToList();
        if (request.Data && InsertStatement(services.Dialect, source.Ref, target, request.Where) is { } insert)
        {
            statements.Add(insert);
        }
        return statements;
    }

    /// <summary>Name of a copied constraint: <c>target_ibfk_1</c>, <c>target_chk_1</c> (like the server-generated ones), up to 64 characters.</summary>
    private static string ConstraintName(string table, string kind, int number)
    {
        var suffix = string.Create(CultureInfo.InvariantCulture, $"_{kind}_{number}");
        var prefix = table.Trim();
        return (prefix.Length + suffix.Length > MaxIdentifier ? prefix[..(MaxIdentifier - suffix.Length)] : prefix) + suffix;
    }
}
