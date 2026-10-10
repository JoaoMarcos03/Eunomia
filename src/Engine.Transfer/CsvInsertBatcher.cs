using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Eunomia.Engine.Transfer;

/// <summary>Tratamento de linhas duplicadas no CSV import (anexo B §3.1; omissão REPLACE).</summary>
public enum CsvDuplicates
{
    Insert,
    InsertIgnore,
    Replace,
}

/// <summary>Coluna de destino de um campo do CSV (mapeamento posicional: o campo k vai para a k-ésima coluna).</summary>
public sealed record CsvTargetColumn(string Name, bool IsNumeric);

/// <summary>
/// INSERT multi-row do modo cliente do CSV import (anexo B §3.3): <c>INSERT [LOW_PRIORITY] [IGNORE] INTO `db`.`t`
/// (cols) VALUES (…), (…)</c> ou <c>REPLACE</c>, fechado aos 0,5 MiB ou às 10.000 linhas. Campos em falta no fim são
/// NULL e os que sobram ignoram-se. O tipo vem da coluna de destino de cada campo (o HeidiSQL usa a da posição
/// original e troca os tipos quando as colunas são reordenadas).
/// </summary>
public sealed partial class CsvInsertBatcher
{
    public const int DefaultMaxBytes = 512 * 1024;
    public const int DefaultMaxRows = 10_000;

    private readonly IReadOnlyList<CsvTargetColumn> _columns;
    private readonly string _prefix;
    private readonly NumberFormatInfo? _localNumbers;
    private readonly int _maxBytes;
    private readonly int _maxRows;
    private readonly StringBuilder _sql = new();

    /// <param name="localNumbers">Formato dos números locais (ex.: <c>1.234,56</c>); <c>null</c> sem a opção.</param>
    public CsvInsertBatcher(
        string database,
        string table,
        IReadOnlyList<CsvTargetColumn> columns,
        CsvDuplicates duplicates = CsvDuplicates.Replace,
        bool lowPriority = false,
        NumberFormatInfo? localNumbers = null,
        int maxBytes = DefaultMaxBytes,
        int maxRows = DefaultMaxRows)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is needed.", nameof(columns));
        }
        _columns = columns;
        _localNumbers = localNumbers;
        _maxBytes = maxBytes;
        _maxRows = Math.Max(1, maxRows);
        var keyword = duplicates == CsvDuplicates.Replace ? "REPLACE" : "INSERT";
        _prefix = keyword + (lowPriority ? " LOW_PRIORITY" : string.Empty) + (duplicates == CsvDuplicates.InsertIgnore ? " IGNORE" : string.Empty)
            + " INTO " + SqlDumpWriter.Quote(database) + "." + SqlDumpWriter.Quote(table)
            + " (" + string.Join(", ", columns.Select(c => SqlDumpWriter.Quote(c.Name))) + ") VALUES ";
    }

    /// <summary>Linhas no INSERT por enviar.</summary>
    public int PendingRows { get; private set; }

    /// <summary>Junta um registo; devolve o INSERT anterior quando este já não cabe nele.</summary>
    public string? Add(IReadOnlyList<string?> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var row = new StringBuilder("(");
        for (var c = 0; c < _columns.Count; c++)
        {
            if (c > 0)
            {
                row.Append(", ");
            }
            AppendValue(row, c < record.Count ? record[c] : null, _columns[c].IsNumeric);
        }
        row.Append(')');

        string? done = null;
        if (PendingRows > 0 && (_sql.Length + row.Length + 2 > _maxBytes || PendingRows >= _maxRows))
        {
            done = Flush();
        }
        if (PendingRows == 0)
        {
            _sql.Append(_prefix);
        }
        else
        {
            _sql.Append(", ");
        }
        _sql.Append(row);
        PendingRows++;
        return done;
    }

    /// <summary>O INSERT por enviar (fim do ficheiro), ou <c>null</c> se não há linhas.</summary>
    public string? Flush()
    {
        if (PendingRows == 0)
        {
            return null;
        }
        var sql = _sql.ToString();
        _sql.Clear();
        PendingRows = 0;
        return sql;
    }

    private void AppendValue(StringBuilder sb, string? value, bool numeric)
    {
        if (value is null)
        {
            sb.Append("NULL");
            return;
        }
        if (numeric)
        {
            var number = _localNumbers is null ? value.Trim() : UnformatNumber(value, _localNumbers);
            if (NumericLiteral().IsMatch(number))
            {
                sb.Append(number);
                return;
            }
        }
        AppendString(sb, value);
    }

    /// <summary>Número local para o formato do SQL: sem separador de milhares e com ponto decimal.</summary>
    public static string UnformatNumber(string value, NumberFormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(format);
        var text = value.Trim();
        if (format.NumberGroupSeparator.Length > 0)
        {
            text = text.Replace(format.NumberGroupSeparator, string.Empty, StringComparison.Ordinal);
            // Muitas culturas usam um espaço inseparável como separador de milhares; o ficheiro pode trazer outro espaço.
            if (string.IsNullOrWhiteSpace(format.NumberGroupSeparator))
            {
                text = string.Concat(text.Where(c => c is not (' ' or ' ' or ' ')));
            }
        }
        return format.NumberDecimalSeparator == "." ? text : text.Replace(format.NumberDecimalSeparator, ".", StringComparison.Ordinal);
    }

    /// <summary>Texto entre plicas com os escapes do MySQL.</summary>
    internal static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('\'');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\'':
                    sb.Append("\\'");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\0':
                    sb.Append("\\0");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('\'');
    }

    [GeneratedRegex(@"^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericLiteral();
}
