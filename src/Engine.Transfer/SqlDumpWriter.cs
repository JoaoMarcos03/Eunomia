using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Eunomia.Engine.Results;

namespace Eunomia.Engine.Transfer;

/// <summary>O que o SQL export escreve dos dados (anexo B §2.3.1; não há modo UPDATE).</summary>
public enum DumpDataMode
{
    None,
    DeleteInsert,
    Insert,
    InsertIgnore,
    Replace,
}

/// <summary>Opções do SQL export com os defaults do HeidiSQL (anexo B §2.3.1).</summary>
public sealed record SqlDumpOptions
{
    public bool AddComments { get; init; } = true;

    public bool DropDatabases { get; init; }

    public bool CreateDatabases { get; init; }

    public bool DropTables { get; init; }

    public bool CreateTables { get; init; }

    public DumpDataMode Data { get; init; } = DumpDataMode.None;

    /// <summary>Max INSERT size em KB; 0 = um INSERT por linha. O INSERT fecha a 90% deste tamanho.</summary>
    public int MaxInsertSizeKb { get; init; } = 1024;

    /// <summary>Linhas por INSERT (10.000 no MySQL, MariaDB, PostgreSQL e SQLite).</summary>
    public int MaxRowsPerInsert { get; init; } = 10_000;

    public bool WrapInTransactions { get; init; }

    public bool RemoveAutoIncrement { get; init; }

    public bool RemoveDefiner { get; init; } = true;

    /// <summary>Drop implica Create (o diálogo marca as duas).</summary>
    internal bool WritesDatabases => CreateDatabases || DropDatabases;

    internal bool WritesStructure => CreateTables || DropTables;
}

/// <summary>Dados do cabeçalho do ficheiro (Host, Server version, Server OS e versão do programa).</summary>
public sealed record DumpHeader(string Host, string ServerVersion, string ServerOs, string AppVersion, string Charset = "utf8mb4");

/// <summary>Coluna de uma view para a tabela temporária que resolve as dependências entre views.</summary>
public sealed record DumpColumn(string Name, string NativeType, bool IsNullable, string? Collation = null);

/// <summary>
/// Escreve um dump SQL no formato do HeidiSQL (FAM-E06; anexo B §2.3.2–2.3.4): cabeçalho e rodapé com os
/// <c>SET</c> condicionais, blocos por objeto com <c>-- Dumping …</c>, DDL com <c>IF NOT EXISTS</c> e quebras de linha
/// CRLF, <c>DELIMITER //</c> para routines, triggers e events, e INSERT multi-row até 90% do Max INSERT size ou
/// 10.000 linhas. Tudo em UTF-8 sem BOM. A ordem dos objetos é de quem chama.
/// </summary>
public sealed partial class SqlDumpWriter
{
    private const string Crlf = "\r\n";
    private const string Rule = "-- --------------------------------------------------------";

    private readonly Utf8Output _output;
    private readonly SqlDumpOptions _options;
    private readonly ByteBuilder _row = new();

    // INSERT em curso.
    private byte[]? _insertPrefix;
    private SqlLiteralKind[] _kinds = [];
    private int[] _scales = [];
    private bool _inData;
    private int _statementRows;
    private long _statementBytes;

    public SqlDumpWriter(Utf8Output output, SqlDumpOptions options)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        _output = output;
        _options = options;
    }

    public SqlDumpOptions Options => _options;

    /// <summary>Linhas escritas na tabela em curso (ou na última).</summary>
    public long TableRows { get; private set; }

    public void WriteHeader(DumpHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var sb = new StringBuilder();
        if (_options.AddComments)
        {
            sb.Append(Rule).Append(Crlf);
            HeaderLine(sb, "Host:", header.Host);
            HeaderLine(sb, "Server version:", header.ServerVersion);
            HeaderLine(sb, "Server OS:", header.ServerOs);
            HeaderLine(sb, "Eunomia Version:", header.AppVersion);
            sb.Append(Rule).Append(Crlf).Append(Crlf);
        }
        sb.Append("/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;").Append(Crlf);
        if (header.Charset.Equals("utf8mb4", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("/*!40101 SET NAMES utf8 */;").Append(Crlf);
            sb.Append("/*!50503 SET NAMES utf8mb4 */;").Append(Crlf);
        }
        else
        {
            sb.Append("/*!40101 SET NAMES ").Append(header.Charset).Append(" */;").Append(Crlf);
        }
        sb.Append("/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;").Append(Crlf);
        sb.Append("/*!40103 SET TIME_ZONE='+00:00' */;").Append(Crlf);
        sb.Append("/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;").Append(Crlf);
        sb.Append("/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;").Append(Crlf);
        sb.Append("/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;").Append(Crlf);
        _output.Write(sb.ToString());
    }

    public void WriteFooter()
    {
        EnsureNoData();
        _output.Write(Crlf
            + "/*!40103 SET TIME_ZONE=IFNULL(@OLD_TIME_ZONE, 'system') */;" + Crlf
            + "/*!40101 SET SQL_MODE=IFNULL(@OLD_SQL_MODE, '') */;" + Crlf
            + "/*!40014 SET FOREIGN_KEY_CHECKS=IFNULL(@OLD_FOREIGN_KEY_CHECKS, 1) */;" + Crlf
            + "/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;" + Crlf
            + "/*!40111 SET SQL_NOTES=IFNULL(@OLD_SQL_NOTES, 1) */;" + Crlf);
    }

    /// <summary>
    /// Bloco da base (só com Create ou Drop): <c>DROP DATABASE</c>, o <c>SHOW CREATE DATABASE</c> com
    /// <c>IF NOT EXISTS</c> e <c>USE</c>.
    /// </summary>
    public void WriteDatabase(string database, string createDatabase)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(createDatabase);
        EnsureNoData();
        if (!_options.WritesDatabases)
        {
            return;
        }
        var sb = new StringBuilder(Crlf + Crlf);
        Comment(sb, "Dumping database structure for " + database);
        if (_options.DropDatabases)
        {
            sb.Append("DROP DATABASE IF EXISTS ").Append(Quote(database)).Append(';').Append(Crlf);
        }
        sb.Append(FixNewlines(CreateDatabasePrefix().Replace(createDatabase, "CREATE DATABASE IF NOT EXISTS ", 1))).Append(';').Append(Crlf);
        sb.Append("USE ").Append(Quote(database)).Append(';').Append(Crlf);
        _output.Write(sb.ToString());
    }

    /// <summary>Estrutura de uma tabela (só com Create ou Drop): o <c>SHOW CREATE TABLE</c> com <c>IF NOT EXISTS</c>.</summary>
    public void WriteTable(string database, string table, string createTable)
    {
        ArgumentNullException.ThrowIfNull(createTable);
        EnsureNoData();
        if (!_options.WritesStructure)
        {
            return;
        }
        var sb = ObjectBlock("table", database, table, "TABLE");
        var code = CreateTablePrefix().Replace(createTable, "CREATE TABLE IF NOT EXISTS ", 1);
        if (_options.RemoveAutoIncrement)
        {
            code = AutoIncrementClause().Replace(code, string.Empty);
        }
        sb.Append(FixNewlines(code)).Append(';').Append(Crlf);
        _output.Write(sb.ToString());
    }

    /// <summary>
    /// Primeira passagem de uma view: tabela temporária com as colunas da view (sem DEFAULT, VARCHAR com comprimento
    /// 1), para que as views que dependem dela se criem por qualquer ordem.
    /// </summary>
    public void WriteViewPlaceholder(string database, string view, IReadOnlyList<DumpColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        EnsureNoData();
        if (!_options.WritesStructure)
        {
            return;
        }
        var sb = ObjectBlock("view", database, view, "VIEW");
        Comment(sb, "Creating temporary table to overcome VIEW dependency errors");
        sb.Append("CREATE TABLE ").Append(Quote(view)).Append(" (").Append(Crlf);
        for (var i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            sb.Append('\t').Append(Quote(c.Name)).Append(' ').Append(PlaceholderType(c.NativeType))
                .Append(c.IsNullable ? " NULL" : " NOT NULL");
            if (!string.IsNullOrEmpty(c.Collation))
            {
                sb.Append(" COLLATE '").Append(c.Collation).Append('\'');
            }
            sb.Append(i < columns.Count - 1 ? "," : string.Empty).Append(Crlf);
        }
        sb.Append(") ENGINE=MyISAM;").Append(Crlf);
        _output.Write(sb.ToString());
    }

    /// <summary>Segunda passagem de uma view (depois dos triggers): larga a tabela temporária e cria a view.</summary>
    public void WriteView(string database, string view, string createView)
    {
        ArgumentNullException.ThrowIfNull(createView);
        EnsureNoData();
        if (!_options.WritesStructure)
        {
            return;
        }
        var sb = new StringBuilder(Crlf);
        Comment(sb, "Removing temporary table and create final VIEW structure");
        sb.Append("DROP TABLE IF EXISTS ").Append(Quote(view)).Append(';').Append(Crlf);
        sb.Append(FixNewlines(StripDefiner(createView))).Append(';').Append(Crlf);
        _output.Write(sb.ToString());
    }

    /// <summary>Trigger com o SQL_MODE com que foi criado, entre <c>DELIMITER //</c> e <c>DELIMITER ;</c>.</summary>
    public void WriteTrigger(string database, string trigger, string sqlMode, string createTrigger)
    {
        ArgumentNullException.ThrowIfNull(sqlMode);
        ArgumentNullException.ThrowIfNull(createTrigger);
        EnsureNoData();
        if (!_options.WritesStructure)
        {
            return;
        }
        var sb = ObjectBlock("trigger", database, trigger, "TRIGGER");
        sb.Append("SET @OLDTMP_SQL_MODE=@@SQL_MODE, SQL_MODE='").Append(sqlMode.Replace("'", "''", StringComparison.Ordinal)).Append("';").Append(Crlf);
        Delimited(sb, createTrigger);
        sb.Append("SET SQL_MODE=@OLDTMP_SQL_MODE;").Append(Crlf);
        _output.Write(sb.ToString());
    }

    /// <summary>Function, procedure ou event entre <c>DELIMITER //</c> e <c>DELIMITER ;</c>.</summary>
    /// <param name="objectType"><c>function</c>, <c>procedure</c> ou <c>event</c>.</param>
    public void WriteRoutine(string database, string objectType, string name, string create)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentNullException.ThrowIfNull(create);
        EnsureNoData();
        if (!_options.WritesStructure)
        {
            return;
        }
        var sb = ObjectBlock(objectType.ToLowerInvariant(), database, name, objectType.ToUpperInvariant());
        Delimited(sb, create);
        _output.Write(sb.ToString());
    }

    /// <summary>Erro ao ler o DDL de um objeto: fica no ficheiro como comentário (anexo B §2.3.3).</summary>
    public void WriteError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureNoData();
        _output.Write("/* " + message.Replace("*/", "* /", StringComparison.Ordinal) + " */" + Crlf);
    }

    /// <summary>Tabela cujos dados não se exportam (motor MRG_MYISAM ou FEDERATED).</summary>
    public void WriteDataSkipped(string database, string table, string engine)
    {
        EnsureNoData();
        if (_options.Data == DumpDataMode.None)
        {
            return;
        }
        var sb = new StringBuilder(Crlf);
        Comment(sb, $"Table {database}.{table} uses the {engine} engine, data export skipped.");
        _output.Write(sb.ToString());
    }

    /// <summary>
    /// Começa os dados de uma tabela: o comentário com a estimativa de linhas, <c>DELETE FROM</c> no modo
    /// "Delete + insert" e <c>BEGIN</c> com "Wrap data DML in transactions". Devolve <c>false</c> em "No data" (só o
    /// comentário <c>-- Data exporting was unselected.</c>): não há linhas a pedir.
    /// </summary>
    /// <param name="columns">Colunas do INSERT, na ordem dos blocos que se seguem (sem as colunas geradas).</param>
    public bool BeginData(string database, string table, long estimatedRows, bool approximate, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        EnsureNoData();
        TableRows = 0;
        var sb = new StringBuilder(Crlf);
        if (_options.Data == DumpDataMode.None)
        {
            Comment(sb, "Data exporting was unselected.");
            _output.Write(sb.ToString());
            return false;
        }
        var rows = estimatedRows.ToString("#,0", CultureInfo.InvariantCulture);
        Comment(sb, approximate ? $"Dumping data for table {database}.{table}: ~{rows} rows (approximately)" : $"Dumping data for table {database}.{table}: {rows} rows");
        var quoted = Quote(table);
        if (_options.WrapInTransactions)
        {
            sb.Append("BEGIN;").Append(Crlf);
        }
        if (_options.Data == DumpDataMode.DeleteInsert)
        {
            sb.Append("DELETE FROM ").Append(quoted).Append(';').Append(Crlf);
            if (_options.RemoveAutoIncrement)
            {
                sb.Append("/*!50000 ALTER TABLE ").Append(quoted).Append(" AUTO_INCREMENT = 1 */;").Append(Crlf);
            }
        }
        _output.Write(sb.ToString());

        var keyword = _options.Data switch
        {
            DumpDataMode.InsertIgnore => "INSERT IGNORE INTO ",
            DumpDataMode.Replace => "REPLACE INTO ",
            _ => "INSERT INTO ",
        };
        _insertPrefix = Encoding.UTF8.GetBytes(keyword + quoted + " (" + string.Join(", ", columns.Select(Quote)) + ") VALUES");
        _kinds = [];
        _scales = [];
        _inData = true;
        _statementRows = 0;
        _statementBytes = 0;
        return true;
    }

    /// <summary>Junta as linhas de um bloco ao INSERT em curso, fechando-o e abrindo outro quando chega ao limite.</summary>
    public void WriteRows(RowBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (!_inData)
        {
            throw new InvalidOperationException("BeginData was not called.");
        }
        if (_kinds.Length != block.Schema.Count)
        {
            _kinds = [.. block.Schema.Columns.Select(SqlLiteralWriter.KindOf)];
            _scales = [.. block.Schema.Columns.Select(c => c.Scale)];
        }
        var single = _options.MaxInsertSizeKb <= 0;
        var maxBytes = (long)(_options.MaxInsertSizeKb * 1024L * 0.9);
        var maxRows = Math.Max(1, _options.MaxRowsPerInsert);
        for (var r = 0; r < block.RowCount; r++)
        {
            _row.Clear();
            _row.Append((byte)'(');
            for (var c = 0; c < _kinds.Length; c++)
            {
                if (c > 0)
                {
                    _row.Append(", "u8);
                }
                SqlLiteralWriter.Write(_row, block, r, c, _kinds[c], _scales[c]);
            }
            _row.Append((byte)')');

            if (single)
            {
                _output.Write(_insertPrefix!);
                _output.Write((byte)' ');
                _output.Write(_row.Span);
                _output.Write(";\r\n"u8);
            }
            else
            {
                if (_statementRows > 0 && (_statementBytes + 4 + _row.Length > maxBytes || _statementRows >= maxRows))
                {
                    CloseStatement();
                }
                if (_statementRows == 0)
                {
                    _output.Write(_insertPrefix!);
                    _output.Write("\r\n\t"u8);
                    _statementBytes = _insertPrefix!.Length + 3;
                }
                else
                {
                    _output.Write(",\r\n\t"u8);
                    _statementBytes += 4;
                }
                _output.Write(_row.Span);
                _statementBytes += _row.Length;
                _statementRows++;
            }
            TableRows++;
        }
    }

    /// <summary>Fecha o INSERT em curso (também depois de um cancel: a tabela fica parcial) e o <c>COMMIT</c>.</summary>
    public void EndData()
    {
        if (!_inData)
        {
            return;
        }
        if (_statementRows > 0)
        {
            CloseStatement();
        }
        if (_options.WrapInTransactions)
        {
            _output.Write("COMMIT;\r\n"u8);
        }
        _inData = false;
        _insertPrefix = null;
    }

    private void CloseStatement()
    {
        _output.Write(";\r\n"u8);
        _statementRows = 0;
        _statementBytes = 0;
    }

    private void EnsureNoData()
    {
        if (_inData)
        {
            throw new InvalidOperationException("EndData was not called.");
        }
    }

    private StringBuilder ObjectBlock(string objectType, string database, string name, string dropType)
    {
        var sb = new StringBuilder(Crlf);
        Comment(sb, $"Dumping structure for {objectType} {database}.{name}");
        if (_options.DropTables)
        {
            sb.Append("DROP ").Append(dropType).Append(" IF EXISTS ").Append(Quote(name)).Append(';').Append(Crlf);
        }
        return sb;
    }

    private void Delimited(StringBuilder sb, string create)
    {
        sb.Append("DELIMITER //").Append(Crlf);
        sb.Append(FixNewlines(StripDefiner(create))).Append("//").Append(Crlf);
        sb.Append("DELIMITER ;").Append(Crlf);
    }

    private void Comment(StringBuilder sb, string text)
    {
        if (_options.AddComments)
        {
            sb.Append("-- ").Append(text).Append(Crlf);
        }
    }

    private string StripDefiner(string code) => _options.RemoveDefiner ? DefinerClause().Replace(code, string.Empty) : code;

    /// <summary>Identificador sempre entre backticks, como no dump do HeidiSQL.</summary>
    internal static string Quote(string name) => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";

    private static void HeaderLine(StringBuilder sb, string label, string value) =>
        sb.Append("-- ").Append(label.PadRight(30)).Append(value).Append(Crlf);

    /// <summary>Quebras de linha CRLF, como o <c>fixNewlines</c> do HeidiSQL.</summary>
    internal static string FixNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", Crlf, StringComparison.Ordinal);

    /// <summary>Tipo da coluna na tabela temporária: em maiúsculas fora das aspas, VARCHAR com comprimento 1.</summary>
    internal static string PlaceholderType(string nativeType)
    {
        var sb = new StringBuilder(nativeType.Length);
        var quote = '\0';
        foreach (var c in nativeType)
        {
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
                sb.Append(c);
                continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
            }
            sb.Append(char.ToUpperInvariant(c));
        }
        return VarcharLength().Replace(sb.ToString(), "VARCHAR(1)");
    }

    [GeneratedRegex(@"^\s*CREATE\s+DATABASE\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateDatabasePrefix();

    [GeneratedRegex(@"^\s*CREATE\s+TABLE\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTablePrefix();

    // O HeidiSQL exige um espaço depois do número; aqui também apanha a cláusula no fim do texto.
    [GeneratedRegex(@"\sAUTO_INCREMENT\s*=\s*\d+(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoIncrementClause();

    [GeneratedRegex(@"\sDEFINER\s*=\s*\S+(?=\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DefinerClause();

    [GeneratedRegex(@"^VARCHAR\(\d+\)", RegexOptions.CultureInvariant)]
    private static partial Regex VarcharLength();
}
