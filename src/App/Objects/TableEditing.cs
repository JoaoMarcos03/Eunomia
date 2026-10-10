using System.Globalization;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Results;

namespace Eunomia.App.Objects;

/// <summary>
/// O que o table editor precisa do servidor (anexo C §5): as listas (collations, engines, tabelas da base) e a gravação
/// (CREATE numa tabela nova, ALTER por diff numa existente), na ligação primária para o SQL ir para o log.
/// </summary>
public static class TableEditing
{
    /// <summary>Collations (todas, ordenadas), engines disponíveis e tabelas da base.</summary>
    public static async Task<(IReadOnlyList<string> Collations, IReadOnlyList<string> Engines, IReadOnlyList<string> Tables)> ListsAsync(
        OpenSession session, string database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Collations e engines não mudam na sessão: uma ida ao servidor na primeira tabela, não em cada uma.
        var collations = await session.Metadata.GetServerListAsync("collations", (h, ct) => h.Metadata.GetCollationsAsync(ct), cancellationToken).ConfigureAwait(false);
        var engines = await session.Metadata.GetServerListAsync("engines", (h, ct) => QueryRows.ReadAsync(h, "SHOW ENGINES", ct), cancellationToken).ConfigureAwait(false);
        var objects = await session.Metadata.GetObjectsAsync(database, cancellationToken).ConfigureAwait(false);
        return (
            [.. collations.Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase)],
            [.. engines.Where(r => r.Length > 1 && !string.Equals(r[1], "NO", StringComparison.OrdinalIgnoreCase) && !string.Equals(r[1], "DISABLED", StringComparison.OrdinalIgnoreCase)).Select(r => r[0] ?? string.Empty)],
            [.. objects.Objects.Where(o => o.Ref.Kind == DbObjectKind.Table).Select(o => o.Ref.Name)]);
    }

    /// <summary>Instruções da gravação: CREATE (sem <paramref name="baseline"/>) ou ALTER por diff.</summary>
    public static IReadOnlyList<string> Statements(IServerServices services, TableDefinition? baseline, TableDesign design)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(design);
        var wanted = design.ToDefinition(services.Types, services.Dialect);
        return baseline is null ? services.Ddl.Create(wanted) : services.Ddl.Alter(baseline, wanted);
    }

    /// <summary>
    /// Alterações que podem perder dados, para confirmar antes de gravar: colunas apagadas e colunas que mudam de tipo ou de
    /// charset (o modo estrito do <see cref="ApplyAsync"/> faz falhar os cortes, mas não arredondamentos como DOUBLE →
    /// FLOAT ou menos casas decimais).
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

    /// <summary>Famílias em que cada tipo cabe no seguinte (mesmo sinal).</summary>
    private static readonly string[][] Widening =
    [
        ["tinyint", "smallint", "mediumint", "int", "integer", "bigint"],
        ["float", "double", "real"],
        ["char", "varchar", "tinytext", "text", "mediumtext", "longtext"],
        ["binary", "varbinary", "tinyblob", "blob", "mediumblob", "longblob"],
    ];

    /// <summary>
    /// A mudança de tipo não perde valores: o mesmo tipo com comprimento, precisão, escala ou frações maiores, ou um tipo
    /// maior da mesma família com o mesmo sinal (<c>int</c> → <c>bigint</c>, <c>varchar(20)</c> → <c>text</c>).
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
                // int(11) → int: a largura de display não conta; o resto sem argumentos é o mesmo tipo.
                return oldName.EndsWith("int", StringComparison.Ordinal) || (oldArgs.Count == 0 && newArgs.Count == 0);
            }
            if (oldArgs.Count != newArgs.Count)
            {
                return false;
            }
            if (oldName is "decimal" or "numeric" && oldArgs.Count == 2)
            {
                // Casas inteiras e decimais têm de caber as duas.
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
                // char(n)/varchar(n) → text: cabe se n couber nos bytes do tipo de texto (utf8mb4: 4 bytes por carácter).
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
    /// ALTER que repõe as chaves estrangeiras que o primeiro ALTER do <see cref="IDdlComposer.Alter"/> larga (alteradas ou
    /// apagadas), para o caso de o ALTER principal falhar a seguir; <c>null</c> se nenhuma é largada.
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
    /// Corre as instruções por ordem na ligação primária (<see cref="SafeDdl"/>, em modo estrito: um ALTER que cortaria
    /// valores falha; o modo anterior é sempre reposto). A primeira que falha pára e lança; se já tinham corrido
    /// instruções e houver <paramref name="restoreForeignKeys"/>, este corre antes de lançar.
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
                // O primeiro ALTER largou chaves estrangeiras e o seguinte falhou: a tabela fica como estava.
                await h.ExecuteNonQueryAsync(restoreForeignKeys, ct).ConfigureAwait(false);
                throw;
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Linhas de uma consulta pequena (texto bruto, NULL = <c>null</c>), na ligação de metadata.</summary>
    public static Task<List<string?[]>> RowsAsync(SessionHost host, string sql, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.RunMetadataAsync((h, ct) => QueryRows.ReadAsync(h, sql, ct), cancellationToken);
    }
}
