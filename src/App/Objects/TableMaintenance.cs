using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;

namespace Eunomia.App.Objects;

/// <summary>A row of the server's result: table, operation, type (<c>status</c>, <c>error</c>, <c>info</c>, <c>note</c>, <c>warning</c>) and text.</summary>
public sealed record MaintenanceMessage(string Table, string Operation, string Type, string Text)
{
    public bool IsError => string.Equals(Type, "error", StringComparison.OrdinalIgnoreCase);

    public bool IsNote => string.Equals(Type, "note", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Maintenance (Tools ▸ Maintenance and popupDB; appendix B §2.2): one statement per table or view, on a jobs connection
/// (the tree and the query tabs stay free), with the server's result row by row. Other kinds of objects are
/// skipped. OPTIMIZE and REPAIR rebuild the table and lock it while they run: they ask for confirmation first.
/// </summary>
public static class TableMaintenance
{
    /// <summary>Operations that rebuild the tables (ask before running).</summary>
    public static bool Rebuilds(MaintenanceOperation operation) => operation is MaintenanceOperation.Optimize or MaintenanceOperation.Repair;

    /// <summary>Operation text as in HeidiSQL ("Check", "Checksum", …).</summary>
    public static string Caption(MaintenanceOperation operation) => operation switch
    {
        MaintenanceOperation.Check => "Check",
        MaintenanceOperation.Analyze => "Analyze",
        MaintenanceOperation.Checksum => "Checksum",
        MaintenanceOperation.Optimize => "Optimize",
        MaintenanceOperation.Repair => "Repair",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    /// <summary>Question before OPTIMIZE and REPAIR (the session is added by the caller).</summary>
    public static string Question(MaintenanceOperation operation, int tables, MaintenanceOptions options)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{Caption(operation)} {tables} table(s)?\n\n{Caption(operation).ToUpperInvariant()} rebuilds each table and locks it while it runs.");
        if (operation == MaintenanceOperation.Repair && options.HasFlag(MaintenanceOptions.UseFrm))
        {
            text += "\n\nUSE_FRM rebuilds the index file from the table definition and can lose rows of a table that is not damaged.";
        }
        return text;
    }

    /// <summary>Runs the operation on a table or view and returns the server's rows.</summary>
    public static async Task<IReadOnlyList<MaintenanceMessage>> RunAsync(SessionHost host, MaintenanceOperation operation, DbObjectRef table,
        MaintenanceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(table);
        var name = Caption(operation).ToLowerInvariant();
        if (table.Kind is not (DbObjectKind.Table or DbObjectKind.View))
        {
            return [new(table.Name, name, "note", $"Skipped - a {table.Kind.ToString().ToLowerInvariant()} cannot be maintained.")];
        }
        var dialect = host.ServerServices?.Dialect ?? throw new InvalidOperationException("The session is not connected.");
        if (dialect.Maintenance(operation, table, options) is not { } sql)
        {
            return [new(table.Name, name, "note", "(unsupported)")];
        }
        var rows = await host.RunJobAsync((h, ct) => QueryRows.ReadAsync(h, sql, ct), cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => ToMessage(table, name, r))];
    }

    /// <summary>
    /// A server row: <c>Table, Op, Msg_type, Msg_text</c> for CHECK, ANALYZE, OPTIMIZE and REPAIR; <c>Table, Checksum</c>
    /// for CHECKSUM (NULL when the table does not exist).
    /// </summary>
    internal static MaintenanceMessage ToMessage(DbObjectRef table, string operation, IReadOnlyList<string?> row) => row.Count switch
    {
        >= 4 => new(row[0] ?? table.Name, row[1] ?? operation, row[2] ?? string.Empty, row[3] ?? string.Empty),
        2 => new(row[0] ?? table.Name, operation, row[1] is null ? "error" : "status", row[1] ?? "NULL"),
        _ => new(table.Name, operation, "status", string.Join(" ", row.Select(v => v ?? "NULL"))),
    };
}
