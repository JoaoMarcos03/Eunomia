using System.Globalization;
using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Objects;

/// <summary>
/// Pedido do Copy table: base e nome do destino, as colunas, índices e chaves estrangeiras escolhidos, se copia os dados
/// e o filtro WHERE dos dados (anexo B §5.1).
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
/// Copy table (Create new ▸ Table copy; anexo B §5.1): um <c>CREATE TABLE</c> pelo composer com os elementos escolhidos e,
/// com os dados, um <c>INSERT … SELECT</c> que corre todo no servidor. O destino nunca é uma tabela que já existe: o
/// <c>CREATE TABLE</c> não leva <c>IF NOT EXISTS</c> (o servidor recusa e o INSERT não corre) e quem pede é avisado antes
/// (o HeidiSQL oferecia apagar o destino; aqui é preciso fazer Drop à parte).
/// </summary>
public static class TableCopy
{
    /// <summary>Comprimento máximo de um identificador (MySQL e MariaDB).</summary>
    private const int MaxIdentifier = 64;

    /// <summary>Nome por omissão do destino.</summary>
    public static string DefaultName(string source) => source + "_copy";

    /// <summary>
    /// Estrutura do destino: só as colunas escolhidas; os índices e as chaves estrangeiras escolhidos cujas colunas foram
    /// todas copiadas; os checks só quando todas as colunas foram copiadas (a expressão pode usar qualquer coluna). Uma
    /// coluna AUTO_INCREMENT sem um índice copiado que comece por ela perde o AUTO_INCREMENT (senão o servidor recusa a
    /// tabela, ER_WRONG_AUTO_KEY). Na mesma base, as chaves estrangeiras e os checks ficam com nomes novos a partir do
    /// destino, porque os nomes destas restrições são únicos por base.
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
    /// <c>INSERT INTO destino (colunas) SELECT colunas FROM origem [WHERE filtro]</c>, sem as colunas geradas (o servidor
    /// calcula-as); <c>null</c> se não houver colunas para copiar. O filtro passa pelas regras do filtro do Data tab (uma
    /// só condição, sem <c>;</c> a meio).
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

    /// <summary>Instruções do Copy table, por ordem: o CREATE e, com os dados, o INSERT … SELECT.</summary>
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

    /// <summary>Nome de uma restrição copiada: <c>destino_ibfk_1</c>, <c>destino_chk_1</c> (como os gerados pelo servidor), até 64 caracteres.</summary>
    private static string ConstraintName(string table, string kind, int number)
    {
        var suffix = string.Create(CultureInfo.InvariantCulture, $"_{kind}_{number}");
        var prefix = table.Trim();
        return (prefix.Length + suffix.Length > MaxIdentifier ? prefix[..(MaxIdentifier - suffix.Length)] : prefix) + suffix;
    }
}
