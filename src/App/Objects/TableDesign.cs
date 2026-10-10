using System.Globalization;
using System.Text;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Objects;

/// <summary>Tipos do "Default" do table editor (anexo C §5.2, <c>TColumnDefaultEditorLink</c>).</summary>
public enum DefaultKind
{
    None,
    Text,
    Null,
    Expression,
    AutoIncrement,
}

/// <summary>Uma linha da grelha de colunas do table editor (anexo C §5.2), nos campos que o utilizador edita.</summary>
public sealed class ColumnDesign
{
    /// <summary>Nome no servidor antes de editar; <c>null</c> numa coluna nova.</summary>
    public string? OriginalName { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Tipo sem argumentos, em maiúsculas (<c>VARCHAR</c>).</summary>
    public string DataType { get; set; } = "INT";

    /// <summary>Length/Set: <c>255</c>, <c>10,2</c> ou <c>'a','b'</c>.</summary>
    public string LengthSet { get; set; } = string.Empty;

    public bool Unsigned { get; set; }

    public bool AllowNull { get; set; } = true;

    public bool Zerofill { get; set; }

    public DefaultKind DefaultKind { get; set; } = DefaultKind.None;

    /// <summary>Texto do "Custom text" (sem aspas) ou a expressão.</summary>
    public string DefaultText { get; set; } = string.Empty;

    public string OnUpdate { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public string Collation { get; set; } = string.Empty;

    /// <summary>Expressão de coluna gerada.</summary>
    public string Expression { get; set; } = string.Empty;

    /// <summary>Vazio, <c>VIRTUAL</c>, <c>STORED</c> ou <c>PERSISTENT</c>.</summary>
    public string Virtuality { get; set; } = string.Empty;

    public bool Invisible { get; set; }

    public ColumnDesign Clone() => (ColumnDesign)MemberwiseClone();

    /// <summary>O que a célula Default mostra: "No default", o texto entre aspas, NULL, a expressão ou AUTO_INCREMENT, mais o ON UPDATE.</summary>
    public string DefaultCaption => DefaultKind switch
    {
        DefaultKind.Text => "'" + DefaultText + "'",
        DefaultKind.Null => "NULL",
        DefaultKind.Expression => DefaultText,
        DefaultKind.AutoIncrement => "AUTO_INCREMENT",
        _ => "No default",
    } + (OnUpdate.Length > 0 ? " ON UPDATE " + OnUpdate : string.Empty);

    /// <summary>Tipo nativo completo: <c>varchar(40)</c>, <c>int(10) unsigned zerofill</c>.</summary>
    public string NativeType()
    {
        var sb = new StringBuilder(DataType.ToLowerInvariant());
        if (LengthSet.Trim().Length > 0)
        {
            sb.Append('(').Append(LengthSet.Trim()).Append(')');
        }
        if (Unsigned)
        {
            sb.Append(" unsigned");
        }
        if (Zerofill)
        {
            sb.Append(" zerofill");
        }
        return sb.ToString();
    }
}

/// <summary>Um índice no table editor: colunas com o prefixo opcional (<c>nome(20)</c>).</summary>
public sealed class IndexDesign
{
    public string Name { get; set; } = string.Empty;

    public IndexKind Kind { get; set; } = IndexKind.Key;

    /// <summary>Colunas como o utilizador as escreve: <c>a, b(20)</c>.</summary>
    public string Columns { get; set; } = string.Empty;

    /// <summary>Vazio, BTREE, HASH ou RTREE.</summary>
    public string Algorithm { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;
}

public sealed class ForeignKeyDesign
{
    public string Name { get; set; } = string.Empty;

    public string Columns { get; set; } = string.Empty;

    /// <summary>Tabela referida (na mesma base, ou <c>base.tabela</c>).</summary>
    public string ReferenceTable { get; set; } = string.Empty;

    public string ForeignColumns { get; set; } = string.Empty;

    public ForeignKeyAction OnUpdate { get; set; } = ForeignKeyAction.Restrict;

    public ForeignKeyAction OnDelete { get; set; } = ForeignKeyAction.Restrict;
}

public sealed class CheckDesign
{
    public string Name { get; set; } = string.Empty;

    public string Clause { get; set; } = string.Empty;
}

/// <summary>
/// Modelo editável do table editor (anexo C §5; FAM-E01–E03): Basic (nome, comentário), Options (auto increment,
/// collation com "Convert data", engine, row format, checksum, avg row length, max rows), colunas, índices, chaves
/// estrangeiras e checks. Converte de e para <see cref="TableDefinition"/>; o ALTER sai do <see cref="IDdlComposer"/>.
/// </summary>
public sealed class TableDesign
{
    public string Database { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public string AutoIncrement { get; set; } = string.Empty;

    public string Collation { get; set; } = string.Empty;

    /// <summary>"Convert data": <c>CONVERT TO CHARSET</c> com a collation em vez de <c>COLLATE=</c>.</summary>
    public bool ConvertData { get; set; }

    public string Engine { get; set; } = string.Empty;

    public string RowFormat { get; set; } = string.Empty;

    public string Checksum { get; set; } = string.Empty;

    public string AvgRowLength { get; set; } = string.Empty;

    public string MaxRows { get; set; } = string.Empty;

    public List<ColumnDesign> Columns { get; } = [];

    public List<IndexDesign> Indexes { get; } = [];

    public List<ForeignKeyDesign> ForeignKeys { get; } = [];

    public List<CheckDesign> Checks { get; } = [];

    /// <summary>Tipos do drop-down "Datatype", por categoria (anexo C §5.2).</summary>
    public static IReadOnlyList<(string Category, IReadOnlyList<string> Types)> DataTypes { get; } =
    [
        ("Integer", ["TINYINT", "SMALLINT", "MEDIUMINT", "INT", "BIGINT", "BIT"]),
        ("Real", ["FLOAT", "DOUBLE", "DECIMAL"]),
        ("Text", ["CHAR", "VARCHAR", "TINYTEXT", "TEXT", "MEDIUMTEXT", "LONGTEXT", "JSON"]),
        ("Binary", ["BINARY", "VARBINARY", "TINYBLOB", "BLOB", "MEDIUMBLOB", "LONGBLOB"]),
        ("Temporal (time)", ["DATE", "TIME", "YEAR", "DATETIME", "TIMESTAMP"]),
        ("Spatial (geometry)", ["POINT", "LINESTRING", "POLYGON", "GEOMETRY", "MULTIPOINT", "MULTILINESTRING", "MULTIPOLYGON", "GEOMETRYCOLLECTION"]),
        ("Other", ["UNKNOWN", "ENUM", "SET"]),
    ];

    private static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase) { "TINYINT", "SMALLINT", "MEDIUMINT", "INT", "INTEGER", "BIGINT" };

    /// <summary>Unsigned e Zerofill só nos números (não no BIT); AUTO_INCREMENT só nos inteiros.</summary>
    public static bool IsNumeric(string dataType) =>
        IntegerTypes.Contains(dataType) || dataType.ToUpperInvariant() is "FLOAT" or "DOUBLE" or "DECIMAL" or "REAL" or "NUMERIC";

    public static bool IsInteger(string dataType) => IntegerTypes.Contains(dataType);

    /// <summary>Tabela nova numa base (o nome fica por escrever).</summary>
    public static TableDesign New(string database) => new() { Database = database };

    public static TableDesign From(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var design = new TableDesign
        {
            Database = table.Ref.Schema,
            Name = table.Ref.Name,
            Comment = table.Options.GetValueOrDefault("Comment") ?? string.Empty,
            AutoIncrement = table.Options.GetValueOrDefault("Auto_increment") ?? string.Empty,
            Collation = table.Options.GetValueOrDefault("Collation") ?? string.Empty,
            Engine = table.Options.GetValueOrDefault("Engine") ?? string.Empty,
            RowFormat = ExplicitRowFormat(table.Options),
        };
        var create = table.Options.GetValueOrDefault("Create_options") ?? string.Empty;
        design.Checksum = CreateOption(create, "checksum");
        design.AvgRowLength = CreateOption(create, "avg_row_length");
        design.MaxRows = CreateOption(create, "max_rows");
        design.Columns.AddRange(table.Columns.Select(FromColumn));
        design.Indexes.AddRange(table.Indexes.Select(i => new IndexDesign
        {
            Name = i.Name,
            Kind = i.Kind,
            Columns = string.Join(", ", i.Columns.Select((c, n) => i.SubParts is { } sub && n < sub.Count && sub[n] is { } p ? $"{FormatName(c)}({p.ToString(CultureInfo.InvariantCulture)})" : FormatName(c))),
            Algorithm = i.Algorithm ?? string.Empty,
            Comment = i.Comment ?? string.Empty,
        }));
        design.ForeignKeys.AddRange(table.ForeignKeys.Select(f => new ForeignKeyDesign
        {
            Name = f.Name,
            Columns = string.Join(", ", f.Columns.Select(FormatName)),
            ReferenceTable = string.Equals(f.ReferencedTable.Schema, table.Ref.Schema, StringComparison.Ordinal)
                ? FormatName(f.ReferencedTable.Name, dotIsSpecial: true)
                : FormatName(f.ReferencedTable.Schema, dotIsSpecial: true) + "." + FormatName(f.ReferencedTable.Name, dotIsSpecial: true),
            ForeignColumns = string.Join(", ", f.ReferencedColumns.Select(FormatName)),
            OnUpdate = f.OnUpdate,
            OnDelete = f.OnDelete,
        }));
        design.Checks.AddRange(table.Checks.Select(c => new CheckDesign { Name = c.Name, Clause = c.Expression }));
        return design;
    }

    /// <summary><c>ROW_FORMAT</c> só quando foi dado na criação (o <c>Row_format</c> do status é o efetivo).</summary>
    private static string ExplicitRowFormat(IReadOnlyDictionary<string, string> options) =>
        CreateOption(options.GetValueOrDefault("Create_options") ?? string.Empty, "row_format");

    private static string CreateOption(string createOptions, string key)
    {
        foreach (var part in createOptions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && string.Equals(part[..eq], key, StringComparison.OrdinalIgnoreCase))
            {
                return part[(eq + 1)..].Trim('"', '\'');
            }
        }
        return string.Empty;
    }

    public static ColumnDesign FromColumn(ColumnDefinition c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var native = c.NativeType.Trim();
        var paren = native.IndexOf('(', StringComparison.Ordinal);
        var close = native.LastIndexOf(')');
        var space = native.IndexOf(' ', StringComparison.Ordinal);
        var baseName = paren > 0 ? native[..paren] : space > 0 ? native[..space] : native;
        var rest = (close > paren && paren > 0 ? native[(close + 1)..] : space > 0 ? native[space..] : string.Empty).ToLowerInvariant();
        var column = new ColumnDesign
        {
            OriginalName = c.OriginalName ?? c.Name,
            Name = c.Name,
            DataType = baseName.ToUpperInvariant(),
            LengthSet = paren > 0 && close > paren ? native[(paren + 1)..close] : string.Empty,
            Unsigned = rest.Contains("unsigned", StringComparison.Ordinal),
            Zerofill = rest.Contains("zerofill", StringComparison.Ordinal),
            AllowNull = c.IsNullable,
            Comment = c.Comment ?? string.Empty,
            Collation = c.Type.Collation ?? string.Empty,
            Expression = c.GenerationExpression ?? string.Empty,
            Virtuality = c.GenerationExpression is null ? string.Empty : c.IsStored ? "STORED" : "VIRTUAL",
            Invisible = c.IsInvisible,
            OnUpdate = c.OnUpdate ?? string.Empty,
        };
        if (c.IsAutoIncrement)
        {
            column.DefaultKind = DefaultKind.AutoIncrement;
        }
        else if (c.DefaultExpression is not { } d)
        {
            column.DefaultKind = DefaultKind.None;
        }
        else if (string.Equals(d, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            column.DefaultKind = DefaultKind.Null;
        }
        else if (c.DefaultIsExpression)
        {
            column.DefaultKind = DefaultKind.Expression;
            column.DefaultText = d;
        }
        else
        {
            column.DefaultKind = DefaultKind.Text;
            column.DefaultText = Unquote(d);
        }
        return column;
    }

    /// <summary>
    /// Texto de um literal SQL: tira as aspas e desfaz <c>''</c> e as barras; o hex com introdutor (<c>_utf8mb4 X'…'</c>,
    /// a forma dos literais com barras) volta a texto UTF-8.
    /// </summary>
    internal static string Unquote(string literal)
    {
        if (HexText(literal) is { } hex)
        {
            return hex;
        }
        if (literal.Length < 2 || literal[0] != '\'' || literal[^1] != '\'')
        {
            return literal;
        }
        var inner = literal[1..^1];
        var sb = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                i++;
                sb.Append(inner[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', 'Z' => '\u001A', var x => x });
            }
            else if (inner[i] == '\'' && i + 1 < inner.Length && inner[i + 1] == '\'')
            {
                sb.Append('\'');
                i++;
            }
            else
            {
                sb.Append(inner[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>A definição pedida pelo editor (para o CREATE ou para o diff do ALTER).</summary>
    public TableDefinition ToDefinition(ITypeMapper types, IDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(dialect);
        var columns = Columns.Select(c => ToColumn(c, types, dialect)).ToList();
        var indexes = Indexes.Select(i =>
        {
            var parts = SplitParts(i.Columns);
            return new IndexDefinition(i.Kind == IndexKind.Primary ? "PRIMARY" : i.Name, i.Kind, [.. parts.Select(p => p.Column)])
            {
                SubParts = parts.Any(p => p.SubPart is not null) ? [.. parts.Select(p => p.SubPart)] : null,
                Algorithm = i.Algorithm.Length > 0 ? i.Algorithm : null,
                Comment = i.Comment.Length > 0 ? i.Comment : null,
            };
        }).ToList();
        var fks = ForeignKeys.Select(f =>
        {
            var names = SplitNames(f.ReferenceTable, '.');
            var (schema, table) = names.Count == 2 ? (names[0], names[1]) : (Database, names.Count == 1 ? names[0] : f.ReferenceTable.Trim());
            return new ForeignKeyDefinition(f.Name, SplitList(f.Columns), new DbObjectRef(schema, schema, table, DbObjectKind.Table), SplitList(f.ForeignColumns), f.OnUpdate, f.OnDelete);
        }).ToList();
        var checks = Checks.Select(c => new CheckDefinition(c.Name, c.Clause)).ToList();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Put(string key, string value)
        {
            if (value.Trim().Length > 0)
            {
                options[key] = value.Trim();
            }
        }
        Put("Comment", Comment);
        Put("Auto_increment", AutoIncrement);
        Put(ConvertData ? "ConvertCollation" : "Collation", Collation);
        Put("Engine", Engine);
        Put("Row_format", RowFormat);
        Put("Checksum", Checksum);
        Put("Avg_row_length", AvgRowLength);
        Put("Max_rows", MaxRows);
        return new TableDefinition(new DbObjectRef(Database, Database, Name, DbObjectKind.Table), columns, indexes, fks, checks, options);
    }

    /// <summary>Texto de <c>_utf8mb4 X'…'</c> ou <c>_utf8 X'…'</c>, ou <c>null</c> se não for essa forma.</summary>
    private static string? HexText(string literal)
    {
        var at = literal.IndexOf(" X'", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || !literal.StartsWith("_utf8", StringComparison.OrdinalIgnoreCase) || literal[^1] != '\''
            || literal[..at] is not ("_utf8" or "_utf8mb4" or "_utf8mb3"))
        {
            return null;
        }
        var digits = literal[(at + 3)..^1];
        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(digits));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ColumnDefinition ToColumn(ColumnDesign c, ITypeMapper types, IDialect dialect)
    {
        var native = c.NativeType();
        var type = types.Map(native) with { Collation = c.Collation.Length > 0 ? c.Collation : null };
        var generated = c.Expression.Trim().Length > 0;
        string? defaultSql = c.DefaultKind switch
        {
            DefaultKind.Text when IsNumeric(c.DataType) && decimal.TryParse(c.DefaultText, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => c.DefaultText.Trim(),
            DefaultKind.Text => dialect.StringLiteral(c.DefaultText),
            DefaultKind.Null => "NULL",
            DefaultKind.Expression => c.DefaultText.Trim(),
            _ => null,
        };
        return new ColumnDefinition(c.Name, native, type, c.AllowNull, generated ? null : defaultSql, c.DefaultKind == DefaultKind.AutoIncrement && !generated, generated, c.Invisible, c.Comment.Length > 0 ? c.Comment : null)
        {
            DefaultIsExpression = c.DefaultKind == DefaultKind.Expression,
            OnUpdate = c.OnUpdate.Trim().Length > 0 ? c.OnUpdate.Trim() : null,
            GenerationExpression = generated ? c.Expression.Trim() : null,
            IsStored = c.Virtuality is "STORED" or "PERSISTENT",
            OriginalName = c.OriginalName,
        };
    }

    /// <summary>
    /// Nome numa lista do editor (<c>a, b(20)</c>): entre backticks (com <c>``</c>) quando tem vírgulas, parênteses,
    /// backticks, espaços nas pontas ou, nos nomes de tabelas, um ponto; assim um nome nunca se parte em dois ao voltar a ler.
    /// </summary>
    public static string FormatName(string name) => FormatName(name, dotIsSpecial: false);

    private static string FormatName(string name, bool dotIsSpecial) =>
        name.Length == 0 || name.AsSpan().IndexOfAny(",()`") >= 0 || (dotIsSpecial && name.Contains('.', StringComparison.Ordinal)) || name.Trim().Length != name.Length
            ? "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`"
            : name;

    /// <summary><c>a, `b,c`, d(20)</c> → nomes (sem o comprimento do prefixo).</summary>
    public static IReadOnlyList<string> SplitList(string text) => [.. SplitParts(text).Select(p => p.Column)];

    /// <summary><c>a, `b,c`(10)</c> → nomes e comprimentos de prefixo, respeitando os backticks.</summary>
    public static IReadOnlyList<(string Column, int? SubPart)> SplitParts(string text)
    {
        var result = new List<(string, int?)>();
        foreach (var raw in SplitOutsideBackticks(text ?? string.Empty, ','))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                continue;
            }
            int? subPart = null;
            var close = part.Length - 1;
            if (part[close] == ')')
            {
                var open = part.LastIndexOf('(');
                if (open > 0 && int.TryParse(part.AsSpan(open + 1, close - open - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    && (part[open - 1] == '`' || part[..open].IndexOf('`') < 0))
                {
                    subPart = n;
                    part = part[..open].TrimEnd();
                }
            }
            result.Add((UnquoteName(part), subPart));
        }
        return result;
    }

    /// <summary>Nomes separados por <paramref name="separator"/> fora de backticks, sem os backticks.</summary>
    private static List<string> SplitNames(string text, char separator) =>
        [.. SplitOutsideBackticks(text ?? string.Empty, separator).Select(p => UnquoteName(p.Trim())).Where(p => p.Length > 0)];

    private static IEnumerable<string> SplitOutsideBackticks(string text, char separator)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '`')
            {
                quoted = !quoted;
            }
            else if (text[i] == separator && !quoted)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    private static string UnquoteName(string part) =>
        part.Length >= 2 && part[0] == '`' && part[^1] == '`' ? part[1..^1].Replace("``", "`", StringComparison.Ordinal) : part;

    /// <summary>Erros que impedem gravar: nome em falta, coluna sem nome, índice sem colunas.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Name.Trim().Length == 0)
        {
            errors.Add("Please enter a table name.");
        }
        if (Columns.Count == 0)
        {
            errors.Add("A table needs at least one column.");
        }
        errors.AddRange(Columns.Where(c => c.Name.Trim().Length == 0).Select(_ => "A column has no name."));
        errors.AddRange(Indexes.Where(i => SplitList(i.Columns).Count == 0).Select(i => $"Index \"{i.Name}\" has no columns."));
        return errors;
    }
}
