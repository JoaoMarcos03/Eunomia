using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;

namespace Eunomia.App.Objects;

/// <summary>Uma linha do resultado do servidor: tabela, operação, tipo (<c>status</c>, <c>error</c>, <c>info</c>, <c>note</c>, <c>warning</c>) e texto.</summary>
public sealed record MaintenanceMessage(string Table, string Operation, string Type, string Text)
{
    public bool IsError => string.Equals(Type, "error", StringComparison.OrdinalIgnoreCase);

    public bool IsNote => string.Equals(Type, "note", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Maintenance (Tools ▸ Maintenance e popupDB; anexo B §2.2): uma instrução por tabela ou view, numa ligação de jobs (a
/// árvore e as query tabs continuam livres), com o resultado do servidor linha a linha. As outras espécies de objetos são
/// saltadas. OPTIMIZE e REPAIR reconstroem a tabela e bloqueiam-na enquanto correm: pedem confirmação antes.
/// </summary>
public static class TableMaintenance
{
    /// <summary>Operações que reconstroem as tabelas (pergunta antes de correr).</summary>
    public static bool Rebuilds(MaintenanceOperation operation) => operation is MaintenanceOperation.Optimize or MaintenanceOperation.Repair;

    /// <summary>Texto da operação como no HeidiSQL ("Check", "Checksum", …).</summary>
    public static string Caption(MaintenanceOperation operation) => operation switch
    {
        MaintenanceOperation.Check => "Check",
        MaintenanceOperation.Analyze => "Analyze",
        MaintenanceOperation.Checksum => "Checksum",
        MaintenanceOperation.Optimize => "Optimize",
        MaintenanceOperation.Repair => "Repair",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    /// <summary>Pergunta antes de OPTIMIZE e REPAIR (a sessão é acrescentada por quem pergunta).</summary>
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

    /// <summary>Corre a operação numa tabela ou view e devolve as linhas do servidor.</summary>
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
    /// Uma linha do servidor: <c>Table, Op, Msg_type, Msg_text</c> no CHECK, ANALYZE, OPTIMIZE e REPAIR; <c>Table, Checksum</c>
    /// no CHECKSUM (NULL quando a tabela não existe).
    /// </summary>
    internal static MaintenanceMessage ToMessage(DbObjectRef table, string operation, IReadOnlyList<string?> row) => row.Count switch
    {
        >= 4 => new(row[0] ?? table.Name, row[1] ?? operation, row[2] ?? string.Empty, row[3] ?? string.Empty),
        2 => new(row[0] ?? table.Name, operation, row[1] is null ? "error" : "status", row[1] ?? "NULL"),
        _ => new(table.Name, operation, "status", string.Join(" ", row.Select(v => v ?? "NULL"))),
    };
}
