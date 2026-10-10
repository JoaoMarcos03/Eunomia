using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;

namespace Eunomia.App.Objects;

/// <summary>
/// Operações do <c>popupDB</c> sobre objetos e bases (anexo B §6; anexo C §3.3): SQL gerado, textos de confirmação e
/// execução na ligação primária da sessão (as instruções aparecem no SQL log como as do HeidiSQL).
/// </summary>
public static class ObjectActions
{
    /// <summary>Linhas do "Select top N rows" (<c>DatagridRowsPerStep</c>, 1000 por omissão).</summary>
    public const int DefaultRowsPerStep = 1000;

    /// <summary>
    /// Drop de objetos: triggers primeiro, depois views, depois o resto (como o <c>TDBObjectDropComparer</c>), com as
    /// verificações de chaves estrangeiras desligadas à volta.
    /// </summary>
    public static IReadOnlyList<string> DropObjects(IServerServices services, IEnumerable<DbObjectRef> objects)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(objects);
        var drops = objects
            .Select((o, i) => (Obj: o, Index: i))
            .OrderBy(x => DropRank(x.Obj.Kind))
            .ThenBy(x => x.Index)
            .Select(x => services.Ddl.Drop(x.Obj))
            .ToList();
        return WithForeignKeyChecksOff(services.Dialect, drops, disable: true);
    }

    private static int DropRank(DbObjectKind kind) => kind switch
    {
        DbObjectKind.Trigger => 0,
        DbObjectKind.View or DbObjectKind.MaterializedView => 1,
        _ => 2,
    };

    /// <summary>
    /// Empty table(s): um <c>TRUNCATE</c> por tabela, opcionalmente sem verificação de chaves estrangeiras. Nas views,
    /// <c>DELETE FROM</c> (o <c>TRUNCATE</c> do HeidiSQL falha em views; o <c>DELETE</c> funciona nas atualizáveis).
    /// </summary>
    public static IReadOnlyList<string> EmptyTables(IServerServices services, IEnumerable<DbObjectRef> tables, bool disableForeignKeyChecks)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tables);
        var truncates = tables
            .Select(t => t.Kind is DbObjectKind.View or DbObjectKind.MaterializedView
                ? "DELETE FROM " + services.Dialect.QualifiedName(t)
                : services.Dialect.Truncate(t))
            .ToList();
        return WithForeignKeyChecksOff(services.Dialect, truncates, disableForeignKeyChecks);
    }

    private static List<string> WithForeignKeyChecksOff(IDialect dialect, List<string> statements, bool disable)
    {
        if (!disable || dialect.DisableForeignKeyChecks is not { } off || dialect.RestoreForeignKeyChecks is not { } on)
        {
            return statements;
        }
        return [off, .. statements, on];
    }

    /// <summary>"Select top N rows" numa query tab nova; com Shift, o nome vem qualificado com a base.</summary>
    public static string SelectTopRows(IDialect dialect, DbObjectRef table, int rows, bool qualified)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(table);
        var name = qualified ? dialect.QualifiedName(table) : dialect.QuoteIdentifier(table.Name);
        return $"SELECT * FROM {name} {dialect.Paging(0, rows)};";
    }

    /// <summary>
    /// Collation pré-selecionada no Create database (anexo B §5.3): a última usada, a do servidor,
    /// <c>utf8mb4_unicode_ci</c> ou a primeira da lista.
    /// </summary>
    public static string? PreselectCollation(IReadOnlyList<string> collations, string? lastUsed, string? serverDefault)
    {
        ArgumentNullException.ThrowIfNull(collations);
        foreach (var candidate in new[] { lastUsed, serverDefault, "utf8mb4_unicode_ci" })
        {
            if (!string.IsNullOrEmpty(candidate) && collations.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return collations.First(c => string.Equals(c, candidate, StringComparison.OrdinalIgnoreCase));
            }
        }
        return collations.Count > 0 ? collations[0] : null;
    }

    public static string DropObjectsQuestion(string database, IReadOnlyList<DbObjectRef> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        return $"Drop {objects.Count} object(s) in database \"{database}\"?\n\n" + string.Join("\n", objects.Select(o => o.Name));
    }

    public static string DropDatabaseQuestion(string database) =>
        $"Drop Database \"{database}\"?\n\nWARNING: You will lose all objects in database {database}!";

    public static string EmptyTablesQuestion(IReadOnlyList<DbObjectRef> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return $"Empty {tables.Count} table(s) and/or view(s)?\n\n" + string.Join("\n", tables.Select(t => t.Name));
    }

    /// <summary>
    /// Corre as instruções por ordem na ligação primária; o primeiro erro pára as seguintes (como no HeidiSQL), mas a
    /// instrução que repõe as verificações de chaves estrangeiras corre sempre, para a sessão não ficar sem elas.
    /// </summary>
    public static Task RunAsync(SessionHost host, IReadOnlyList<string> statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(statements);
        var restore = host.ServerServices?.Dialect.RestoreForeignKeyChecks;
        return SafeDdl.RunAsync(host, strict: false, async (handle, ct) =>
        {
            var restorePending = false;
            try
            {
                foreach (var sql in statements)
                {
                    if (restore is not null && sql == restore)
                    {
                        restorePending = false;
                    }
                    else if (restore is not null && sql == host.ServerServices!.Dialect.DisableForeignKeyChecks)
                    {
                        restorePending = true;
                    }
                    await handle.ExecuteNonQueryAsync(sql, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                if (restorePending)
                {
                    await handle.ExecuteNonQueryAsync(restore!, CancellationToken.None).ConfigureAwait(false);
                }
            }
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Drop database. Se for a base em <c>USE</c>, muda primeiro para outra (o HeidiSQL faz o mesmo para evitar
    /// "database in use"); sem alternativa, a ligação fica sem base.
    /// </summary>
    public static async Task DropDatabaseAsync(SessionHost host, string database, IReadOnlyList<string> siblings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(siblings);
        var services = host.ServerServices ?? throw new InvalidOperationException("Not connected.");
        if (string.Equals(host.CurrentDatabase, database, StringComparison.Ordinal)
            && siblings.FirstOrDefault(s => !string.Equals(s, database, StringComparison.Ordinal)) is { } other)
        {
            await host.UseDatabaseAsync(other, cancellationToken).ConfigureAwait(false);
        }
        await RunAsync(host, [services.Ddl.DropDatabase(database)], cancellationToken).ConfigureAwait(false);
    }
}
