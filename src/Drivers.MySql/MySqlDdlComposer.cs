using System.Globalization;
using System.Text;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.Drivers.MySql;

/// <summary>
/// CREATE, ALTER e DROP a partir do modelo (secção 14.1; anexo C §5.3). O ALTER segue o <c>ComposeAlterStatement</c> do
/// HeidiSQL: chaves estrangeiras alteradas ou apagadas largam-se num ALTER à parte primeiro; depois as opções da tabela,
/// as colunas pela ordem da lista (<c>CHANGE COLUMN</c> nas alteradas e <c>ADD COLUMN</c> nas novas, com
/// <c>FIRST</c>/<c>AFTER</c>), <c>DROP COLUMN</c>, os índices (largar e voltar a criar os alterados), as chaves
/// estrangeiras e os checks; o rename fica no fim.
/// </summary>
public sealed class MySqlDdlComposer(MySqlDialect dialect) : IDdlComposer
{
    /// <summary>
    /// <c>`nome` tipo [COLLATE c] [NULL|NOT NULL] [DEFAULT …] [ON UPDATE …] [AUTO_INCREMENT] [INVISIBLE] [COMMENT '…']</c>;
    /// colunas geradas: <c>tipo AS (expr) VIRTUAL|STORED</c>.
    /// </summary>
    public string ColumnSpec(ColumnDefinition c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var sb = new StringBuilder();
        sb.Append(dialect.QuoteIdentifier(c.Name)).Append(' ').Append(c.NativeType);
        if (c.Type.Collation is { Length: > 0 } collation && c.Type.Kind is LogicalTypeKind.Text or LogicalTypeKind.Enum or LogicalTypeKind.Set)
        {
            sb.Append(" COLLATE ").Append(collation);
        }
        if (c.GenerationExpression is { Length: > 0 } expression)
        {
            sb.Append(" AS (").Append(expression).Append(c.IsStored ? ") STORED" : ") VIRTUAL");
        }
        else
        {
            sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
            if (c.DefaultExpression is { } d && !c.IsAutoIncrement)
            {
                sb.Append(" DEFAULT ").Append(d);
            }
            if (c.OnUpdate is { Length: > 0 } onUpdate)
            {
                sb.Append(" ON UPDATE ").Append(onUpdate);
            }
            if (c.IsAutoIncrement)
            {
                sb.Append(" AUTO_INCREMENT");
            }
        }
        if (c.IsInvisible)
        {
            sb.Append(" INVISIBLE");
        }
        if (c.Comment is { Length: > 0 } comment)
        {
            sb.Append(" COMMENT ").Append(dialect.PlainStringLiteral(comment));
        }
        return sb.ToString();
    }

    /// <summary><c>PRIMARY KEY (…)</c>, <c>UNIQUE INDEX `n` (…)</c>, <c>INDEX</c>, <c>FULLTEXT</c>, <c>SPATIAL</c>, com prefixos e algoritmo.</summary>
    public string IndexSpec(IndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var parts = string.Join(", ", index.Columns.Select((c, i) =>
            dialect.QuoteIdentifier(c) + (index.SubParts is { } sub && i < sub.Count && sub[i] is { } n ? string.Create(CultureInfo.InvariantCulture, $"({n})") : string.Empty)));
        var name = dialect.QuoteIdentifier(index.Name);
        var sql = index.Kind switch
        {
            IndexKind.Primary => $"PRIMARY KEY ({parts})",
            IndexKind.Unique => $"UNIQUE INDEX {name} ({parts})",
            IndexKind.FullText => $"FULLTEXT INDEX {name} ({parts})",
            IndexKind.Spatial => $"SPATIAL INDEX {name} ({parts})",
            _ => $"INDEX {name} ({parts})",
        };
        if (index.Algorithm is { Length: > 0 } algorithm && index.Kind is not (IndexKind.FullText or IndexKind.Spatial))
        {
            sql += " USING " + algorithm;
        }
        if (index.Comment is { Length: > 0 } comment)
        {
            sql += " COMMENT " + dialect.PlainStringLiteral(comment);
        }
        return sql;
    }

    public string ForeignKeySpec(ForeignKeyDefinition fk)
    {
        ArgumentNullException.ThrowIfNull(fk);
        return $"CONSTRAINT {dialect.QuoteIdentifier(fk.Name)} FOREIGN KEY ({string.Join(", ", fk.Columns.Select(dialect.QuoteIdentifier))}) " +
               $"REFERENCES {dialect.QualifiedName(fk.ReferencedTable)} ({string.Join(", ", fk.ReferencedColumns.Select(dialect.QuoteIdentifier))}) " +
               $"ON UPDATE {Rule(fk.OnUpdate)} ON DELETE {Rule(fk.OnDelete)}";
    }

    public string CheckSpec(CheckDefinition check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return $"CONSTRAINT {dialect.QuoteIdentifier(check.Name)} CHECK ({check.Expression})";
    }

    public IReadOnlyList<string> Create(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var lines = table.Columns.Select(ColumnSpec)
            .Concat(table.Indexes.Select(IndexSpec))
            .Concat(table.ForeignKeys.Select(ForeignKeySpec))
            .Concat(table.Checks.Select(CheckSpec));
        var sql = new StringBuilder();
        sql.Append("CREATE TABLE ").Append(dialect.QualifiedName(table.Ref)).Append(" (\n\t").AppendJoin(",\n\t", lines).Append("\n)");
        foreach (var option in TableOptions(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), table.Options))
        {
            sql.Append('\n').Append(option);
        }
        return [sql.ToString()];
    }

    /// <summary>Opções da tabela que mudaram (ou todas, numa tabela nova), pela ordem do HeidiSQL.</summary>
    private IEnumerable<string> TableOptions(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        string? Changed(string key) =>
            after.TryGetValue(key, out var value) && value.Length > 0 && (!before.TryGetValue(key, out var old) || !string.Equals(old, value, StringComparison.Ordinal))
                ? value
                : null;
        var oldComment = before.GetValueOrDefault("Comment") ?? string.Empty;
        var newComment = after.GetValueOrDefault("Comment") ?? string.Empty;
        if (!string.Equals(oldComment, newComment, StringComparison.Ordinal))
        {
            yield return "COMMENT=" + dialect.PlainStringLiteral(newComment);
        }
        if (after.TryGetValue("ConvertCollation", out var convert) && convert.Length > 0)
        {
            var charset = convert.Split('_')[0];
            yield return $"CONVERT TO CHARSET {charset} COLLATE {convert}";
        }
        else if (Changed("Collation") is { } collation)
        {
            yield return "COLLATE=" + collation;
        }
        // Opções escritas à mão na grelha: só palavras (engine, row format) e números inteiros vão para o DDL.
        if (Changed("Engine") is { } engine && IsWord(engine))
        {
            yield return "ENGINE=" + engine;
        }
        if (Changed("Row_format") is { } rowFormat && IsWord(rowFormat))
        {
            yield return "ROW_FORMAT=" + rowFormat.ToUpperInvariant();
        }
        if (Changed("Checksum") is { } checksum && IsDigits(checksum))
        {
            yield return "CHECKSUM=" + checksum;
        }
        if (Changed("Auto_increment") is { } autoIncrement && IsDigits(autoIncrement))
        {
            yield return "AUTO_INCREMENT=" + autoIncrement;
        }
        if (Changed("Avg_row_length") is { } avg && IsDigits(avg))
        {
            yield return "AVG_ROW_LENGTH=" + avg;
        }
        if (Changed("Max_rows") is { } max && IsDigits(max))
        {
            yield return "MAX_ROWS=" + max;
        }
    }

    private static bool IsDigits(string value) => value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit);

    private static bool IsWord(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public IReadOnlyList<string> Alter(TableDefinition before, TableDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var table = dialect.QualifiedName(before.Ref);
        var statements = new List<string>();

        // 1. Chaves estrangeiras alteradas ou apagadas: largadas primeiro, num ALTER à parte (erro 121 do MySQL).
        var fkAfter = after.ForeignKeys.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var droppedFks = before.ForeignKeys.Where(f => !fkAfter.TryGetValue(f.Name, out var now) || ForeignKeySpec(now) != ForeignKeySpec(f)).ToList();
        if (droppedFks.Count > 0)
        {
            statements.Add($"ALTER TABLE {table}\n\t" + string.Join(",\n\t", droppedFks.Select(f => "DROP FOREIGN KEY " + dialect.QuoteIdentifier(f.Name))));
        }

        var specs = new List<string>();
        // 2. Opções da tabela.
        specs.AddRange(TableOptions(before.Options, after.Options));

        // 3. Colunas pela ordem da lista: CHANGE nas alteradas ou mudadas de sítio, ADD nas novas.
        var oldColumns = before.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var oldOrder = before.Columns.Select(c => c.Name).ToList();
        var kept = after.Columns.Where(c => c.OriginalName is { } o && oldColumns.ContainsKey(o)).Select(c => c.OriginalName!).ToList();
        var survivingOldOrder = oldOrder.Where(n => kept.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        string? previous = null;
        for (var i = 0; i < after.Columns.Count; i++)
        {
            var column = after.Columns[i];
            var position = previous is null ? " FIRST" : " AFTER " + dialect.QuoteIdentifier(previous);
            if (column.OriginalName is { } original && oldColumns.TryGetValue(original, out var old))
            {
                var keptIndex = kept.FindIndex(n => string.Equals(n, original, StringComparison.OrdinalIgnoreCase));
                var moved = keptIndex < 0 || !string.Equals(survivingOldOrder[keptIndex], original, StringComparison.OrdinalIgnoreCase);
                if (moved || ColumnSpec(column) != ColumnSpec(old) || !string.Equals(column.Name, old.Name, StringComparison.Ordinal))
                {
                    specs.Add($"CHANGE COLUMN {dialect.QuoteIdentifier(original)} {ColumnSpec(column)}{position}");
                }
            }
            else
            {
                specs.Add($"ADD COLUMN {ColumnSpec(column)}{position}");
            }
            previous = column.Name;
        }

        // 4. Colunas apagadas.
        foreach (var gone in before.Columns.Where(c => !kept.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
        {
            specs.Add("DROP COLUMN " + dialect.QuoteIdentifier(gone.Name));
        }

        // 5. Índices: apagados e alterados largam-se; alterados e novos criam-se.
        var indexAfter = after.Indexes.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var indexBefore = before.Indexes.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var index in before.Indexes.Where(x => !indexAfter.TryGetValue(x.Name, out var now) || IndexSpec(now) != IndexSpec(x)))
        {
            specs.Add(index.Kind == IndexKind.Primary ? "DROP PRIMARY KEY" : "DROP INDEX " + dialect.QuoteIdentifier(index.Name));
        }
        foreach (var index in after.Indexes.Where(x => !indexBefore.TryGetValue(x.Name, out var old) || IndexSpec(old) != IndexSpec(x)))
        {
            specs.Add("ADD " + IndexSpec(index));
        }

        // 6. Chaves estrangeiras novas ou alteradas; checks.
        var fkBefore = before.ForeignKeys.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        specs.AddRange(after.ForeignKeys.Where(f => !fkBefore.TryGetValue(f.Name, out var old) || ForeignKeySpec(old) != ForeignKeySpec(f)).Select(f => "ADD " + ForeignKeySpec(f)));
        var checkAfter = after.Checks.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var checkBefore = before.Checks.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        specs.AddRange(before.Checks.Where(c => !checkAfter.TryGetValue(c.Name, out var now) || CheckSpec(now) != CheckSpec(c)).Select(c => "DROP CONSTRAINT " + dialect.QuoteIdentifier(c.Name)));
        specs.AddRange(after.Checks.Where(c => !checkBefore.TryGetValue(c.Name, out var old) || CheckSpec(old) != CheckSpec(c)).Select(c => "ADD " + CheckSpec(c)));

        if (specs.Count > 0)
        {
            statements.Add($"ALTER TABLE {table}\n\t" + string.Join(",\n\t", specs));
        }
        // 7. Rename no fim.
        if (!string.Equals(before.Ref.Name, after.Ref.Name, StringComparison.Ordinal))
        {
            statements.Add(dialect.Rename(before.Ref, after.Ref.Name));
        }
        return statements;
    }

    public string Drop(DbObjectRef obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var keyword = obj.Kind switch
        {
            DbObjectKind.View => "VIEW",
            DbObjectKind.Procedure => "PROCEDURE",
            DbObjectKind.Function => "FUNCTION",
            DbObjectKind.Trigger => "TRIGGER",
            DbObjectKind.Event => "EVENT",
            _ => "TABLE",
        };
        return $"DROP {keyword} {dialect.QualifiedName(obj)}";
    }

    /// <summary>Como o preview do HeidiSQL: <c>CREATE DATABASE `x` /*!40100 COLLATE 'coll' */</c> (anexo B §5.3).</summary>
    public string CreateDatabase(string name, string? collation)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sql = "CREATE DATABASE " + dialect.QuoteIdentifier(name);
        return string.IsNullOrEmpty(collation) ? sql : sql + " /*!40100 COLLATE " + dialect.PlainStringLiteral(collation) + " */";
    }

    public string DropDatabase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "DROP DATABASE " + dialect.QuoteIdentifier(name);
    }

    private static string Rule(ForeignKeyAction action) => action switch
    {
        ForeignKeyAction.Cascade => "CASCADE",
        ForeignKeyAction.SetNull => "SET NULL",
        ForeignKeyAction.NoAction => "NO ACTION",
        ForeignKeyAction.SetDefault => "SET DEFAULT",
        _ => "RESTRICT",
    };
}
