using System.Buffers;
using System.Globalization;
using System.Text;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.Drivers.MySql;

/// <summary>
/// MySQL/MariaDB SQL (section 14.1). Identifiers with backticks; text literals that the server reads the same way
/// with and without <c>NO_BACKSLASH_ESCAPES</c> (see <see cref="StringLiteral"/>).
/// </summary>
public sealed class MySqlDialect(ServerIdentity server, bool noBackslashEscapes = false, bool isRds = false) : IDialect
{
    /// <summary>Characters whose escaping depends on <c>NO_BACKSLASH_ESCAPES</c> (the backslash) or that do not go in plain text (NUL, Ctrl+Z).</summary>
    private static readonly SearchValues<char> ModeDependent = SearchValues.Create("\\\0\u001A");

    public ServerIdentity Server { get; } = server;

    /// <summary>The connection the dialect came from had <c>NO_BACKSLASH_ESCAPES</c> (only matters for <see cref="PlainStringLiteral"/>).</summary>
    public bool NoBackslashEscapes { get; } = noBackslashEscapes;

    /// <summary>The connection is to Amazon RDS (KILL through the <c>mysql.rds_*</c> routines).</summary>
    public bool IsRds { get; } = isRds;

    public LikeOperator CaseInsensitiveLike => LikeOperator.Like;

    /// <summary>Types cut in the Data tab (<c>LoadPart</c>): not CHAR, TINYTEXT or TINYBLOB.</summary>
    private static readonly HashSet<string> LoadPartTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "VARCHAR", "TEXT", "MEDIUMTEXT", "LONGTEXT", "JSON", "VARBINARY", "BLOB", "MEDIUMBLOB", "LONGBLOB",
    };

    /// <summary>
    /// <c>LEFT(col, n)</c> for the <c>LoadPart</c> types with a length &gt; n or without a length; JSON goes through
    /// <c>CAST(… AS CHAR)</c> as in HeidiSQL's <c>CastAsText</c>.
    /// </summary>
    public string? LoadPartExpression(ColumnDefinition column, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(column);
        var type = column.NativeType.Trim();
        var paren = type.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? type : type[..paren]).Split(' ')[0];
        if (!LoadPartTypes.Contains(name))
        {
            return null;
        }
        if (paren >= 0)
        {
            var close = type.IndexOf(')', paren);
            if (close > paren && int.TryParse(type.AsSpan(paren + 1, close - paren - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length <= maxLength)
            {
                return null;
            }
        }
        var quoted = QuoteIdentifier(column.Name);
        var inner = string.Equals(name, "JSON", StringComparison.OrdinalIgnoreCase) ? $"CAST({quoted} AS CHAR)" : quoted;
        return string.Create(CultureInfo.InvariantCulture, $"LEFT({inner}, {maxLength})");
    }

    /// <summary>Types that HeidiSQL's <c>CastAsText</c> passes through <c>CAST(… AS CHAR)</c> on MySQL.</summary>
    private static readonly HashSet<string> CastTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATE", "DATETIME", "TIME", "TIMESTAMP", "JSON",
    };

    public string CastAsText(ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var type = column.NativeType.Trim();
        var paren = type.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? type : type[..paren]).Split(' ')[0];
        var quoted = QuoteIdentifier(column.Name);
        return CastTypes.Contains(name) ? $"CAST({quoted} AS CHAR)" : quoted;
    }

    public string? DisableForeignKeyChecks => "SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0";

    public string? RestoreForeignKeyChecks => "SET FOREIGN_KEY_CHECKS=IFNULL(@OLD_FOREIGN_KEY_CHECKS, 1)";

    public DumpSessionSql DumpSession { get; } = new(
        ["SET @OLD_LOCAL_SQL_MODE=@@SQL_MODE, SQL_MODE=''", "SET @OLD_TIME_ZONE=@@TIME_ZONE", "SET TIME_ZONE='+00:00'"],
        ["SET SQL_MODE=@OLD_LOCAL_SQL_MODE", "SET TIME_ZONE=@OLD_TIME_ZONE"],
        ["SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ", "START TRANSACTION /*!40100 WITH CONSISTENT SNAPSHOT */"],
        "ROLLBACK",
        "SELECT @@version_compile_os");

    public string? SessionModeQuery => "SELECT @@SESSION.sql_mode";

    /// <summary><c>SET SESSION TRANSACTION READ ONLY</c> (MySQL 5.6.5+, MariaDB 10.0+): the server refuses DML and DDL.</summary>
    public string? ReadOnlySession =>
        (Server.Engine == DbEngine.MariaDb ? Server.Version >= new Version(10, 0) : Server.Version >= new Version(5, 6, 5))
            ? "SET SESSION TRANSACTION READ ONLY"
            : null;

    /// <summary>
    /// A short <c>lock_wait_timeout</c> (the session value is kept in <c>@EUNOMIA_LWT</c> and restored at the end) and, with
    /// <paramref name="strictFrom"/>, the current mode with <c>STRICT_ALL_TABLES</c> and <c>STRICT_TRANS_TABLES</c> and without
    /// <c>NO_ZERO_DATE</c> or <c>NO_ZERO_IN_DATE</c>: without strict mode MySQL truncates values in an ALTER that narrows a
    /// column (VARCHAR(255) → VARCHAR(10), INT → TINYINT, utf8mb4 → latin1) and only leaves a warning; zero dates the
    /// table already has must not make the copy fail.
    /// </summary>
    public (string Enter, string Restore)? DdlSession(string? strictFrom, int lockWaitSeconds)
    {
        var enter = string.Create(CultureInfo.InvariantCulture, $"SET @EUNOMIA_LWT = @@SESSION.lock_wait_timeout, SESSION lock_wait_timeout = {lockWaitSeconds}");
        var restore = "SET SESSION lock_wait_timeout = @EUNOMIA_LWT";
        if (strictFrom is null)
        {
            return (enter, restore);
        }
        var modes = strictFrom.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(m => !m.Equals("NO_ZERO_DATE", StringComparison.OrdinalIgnoreCase) && !m.Equals("NO_ZERO_IN_DATE", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var strict in new[] { "STRICT_TRANS_TABLES", "STRICT_ALL_TABLES" })
        {
            if (!modes.Contains(strict, StringComparer.OrdinalIgnoreCase))
            {
                modes.Add(strict);
            }
        }
        return (enter + ", SESSION sql_mode = " + StringLiteral(string.Join(',', modes)), restore + ", SESSION sql_mode = " + StringLiteral(strictFrom));
    }

    public string QuoteIdentifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    public string QualifiedName(DbObjectRef obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return string.IsNullOrEmpty(obj.Schema) ? QuoteIdentifier(obj.Name) : QuoteIdentifier(obj.Schema) + "." + QuoteIdentifier(obj.Name);
    }

    /// <summary>
    /// Text literal for an expression, the same with and without <c>NO_BACKSLASH_ESCAPES</c> (the mode may change during the
    /// session, through a <c>SET</c> by the user or the startup script, without the client knowing): doubled quotes
    /// (<c>''</c>), which both modes read the same way; text with a backslash, NUL or Ctrl+Z goes as hex with an introducer
    /// (<c>_utf8mb4 X'…'</c>), which has no escapes. With the old escaping, <c>C:\dir</c> or a line break on a server with
    /// <c>NO_BACKSLASH_ESCAPES</c> was saved with extra backslashes.
    /// </summary>
    public string StringLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.AsSpan().IndexOfAny(ModeDependent) >= 0 ? HexStringLiteral(value) : QuoteDoubled(value);
    }

    /// <summary>
    /// Single-quoted text for the grammar that only accepts a simple string (<c>COMMENT</c>, <c>SHOW</c>'s <c>LIKE</c>,
    /// <c>HELP</c>, <c>LOAD DATA</c> options): the same as <see cref="StringLiteral"/> without backslashes; with backslashes,
    /// the escaping of the mode of the connection the dialect came from.
    /// </summary>
    public string PlainStringLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.AsSpan().IndexOfAny(ModeDependent) < 0 || NoBackslashEscapes)
        {
            return QuoteDoubled(value);
        }
        var sb = new StringBuilder(value.Length + 8);
        sb.Append('\'');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\'':
                    sb.Append("''");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\0':
                    sb.Append("\\0");
                    break;
                case '\u001A':
                    sb.Append("\\Z");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.Append('\'').ToString();
    }

    private static string QuoteDoubled(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary><c>_utf8mb4 X'…'</c> (<c>_utf8</c> on servers without utf8mb4): the text in UTF-8, without escapes.</summary>
    private string HexStringLiteral(string value)
    {
        var v = Server.Version;
        var utf8mb4 = v.Major == 0 || (Server.Engine == DbEngine.MariaDb ? v >= new Version(5, 5) : v >= new Version(5, 5, 3));
        return (utf8mb4 ? "_utf8mb4 X'" : "_utf8 X'") + Convert.ToHexString(Encoding.UTF8.GetBytes(value)) + "'";
    }

    public string Paging(long offset, int count) =>
        offset > 0
            ? string.Create(CultureInfo.InvariantCulture, $"LIMIT {offset}, {count}")
            : string.Create(CultureInfo.InvariantCulture, $"LIMIT {count}");

    public string KeysetPredicate(IReadOnlyList<string> columns, IReadOnlyList<string> literalValues, bool descending)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(literalValues);
        if (columns.Count != literalValues.Count || columns.Count == 0)
        {
            throw new ArgumentException("Keyset columns and values must have the same length.", nameof(literalValues));
        }
        var op = descending ? "<" : ">";
        return columns.Count == 1
            ? $"{QuoteIdentifier(columns[0])} {op} {literalValues[0]}"
            : $"({string.Join(", ", columns.Select(QuoteIdentifier))}) {op} ({string.Join(", ", literalValues)})";
    }

    /// <summary><c>KILL QUERY</c> (≥ 5.0); on RDS, <c>CALL mysql.rds_kill_query</c> (appendix A §10.5).</summary>
    public string KillQuery(long serverConnectionId) =>
        IsRds
            ? string.Create(CultureInfo.InvariantCulture, $"CALL mysql.rds_kill_query({serverConnectionId})")
            : string.Create(CultureInfo.InvariantCulture, $"KILL QUERY {serverConnectionId}");

    /// <summary><c>KILL</c> (<c>qKillProcess</c>); on RDS, <c>CALL mysql.rds_kill</c>.</summary>
    public string KillConnection(long serverConnectionId) =>
        IsRds
            ? string.Create(CultureInfo.InvariantCulture, $"CALL mysql.rds_kill({serverConnectionId})")
            : string.Create(CultureInfo.InvariantCulture, $"KILL {serverConnectionId}");

    public string ChangeDatabase(string database) => "USE " + QuoteIdentifier(database);

    public string Rename(DbObjectRef obj, string newName)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var target = QualifiedName(obj with { Name = newName });
        return obj.Kind switch
        {
            DbObjectKind.Table or DbObjectKind.View => $"RENAME TABLE {QualifiedName(obj)} TO {target}",
            _ => throw new NotSupportedException($"Renaming a {obj.Kind} is not supported on MySQL/MariaDB."),
        };
    }

    public string Truncate(DbObjectRef table) => "TRUNCATE " + QualifiedName(table);

    /// <summary>
    /// <c>&lt;OP&gt; TABLE `db`.`tbl` [QUICK] [FAST] [MEDIUM] [EXTENDED] [CHANGED] [USE_FRM] [FOR UPGRADE]</c> with the options
    /// the operation accepts (appendix B §2.2): CHECK all but USE_FRM; CHECKSUM only QUICK or EXTENDED (one of them); REPAIR
    /// QUICK, EXTENDED and USE_FRM; ANALYZE and OPTIMIZE none.
    /// </summary>
    public string? Maintenance(MaintenanceOperation operation, DbObjectRef table, MaintenanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(table);
        options &= MaintenanceOptionsOf(operation);
        if (operation == MaintenanceOperation.Checksum && options == (MaintenanceOptions.Quick | MaintenanceOptions.Extended))
        {
            // QUICK and EXTENDED exclude each other in CHECKSUM: EXTENDED wins, as it reads the whole table and never gives a wrong result.
            options = MaintenanceOptions.Extended;
        }
        var keyword = operation switch
        {
            MaintenanceOperation.Check => "CHECK",
            MaintenanceOperation.Analyze => "ANALYZE",
            MaintenanceOperation.Checksum => "CHECKSUM",
            MaintenanceOperation.Optimize => "OPTIMIZE",
            MaintenanceOperation.Repair => "REPAIR",
            _ => null,
        };
        if (keyword is null)
        {
            return null;
        }
        var sql = new StringBuilder(keyword).Append(" TABLE ").Append(QualifiedName(table));
        foreach (var (flag, word) in MaintenanceWords)
        {
            if (options.HasFlag(flag))
            {
                sql.Append(' ').Append(word);
            }
        }
        return sql.ToString();
    }

    public MaintenanceOptions MaintenanceOptionsOf(MaintenanceOperation operation) => operation switch
    {
        MaintenanceOperation.Check => MaintenanceOptions.Quick | MaintenanceOptions.Fast | MaintenanceOptions.Medium | MaintenanceOptions.Extended
                                      | MaintenanceOptions.Changed | MaintenanceOptions.ForUpgrade,
        MaintenanceOperation.Checksum => MaintenanceOptions.Quick | MaintenanceOptions.Extended,
        MaintenanceOperation.Repair => MaintenanceOptions.Quick | MaintenanceOptions.Extended | MaintenanceOptions.UseFrm,
        _ => MaintenanceOptions.None,
    };

    private static readonly (MaintenanceOptions Flag, string Word)[] MaintenanceWords =
    [
        (MaintenanceOptions.Quick, "QUICK"),
        (MaintenanceOptions.Fast, "FAST"),
        (MaintenanceOptions.Medium, "MEDIUM"),
        (MaintenanceOptions.Extended, "EXTENDED"),
        (MaintenanceOptions.Changed, "CHANGED"),
        (MaintenanceOptions.UseFrm, "USE_FRM"),
        (MaintenanceOptions.ForUpgrade, "FOR UPGRADE"),
    ];

    public string Explain(string statement, string format) =>
        string.Equals(format, "TRADITIONAL", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(format)
            ? "EXPLAIN " + statement
            : $"EXPLAIN FORMAT={format} {statement}";

    /// <summary>
    /// <c>SHOW TABLE STATUS … LIKE</c> like HeidiSQL; with <c>NO_BACKSLASH_ESCAPES</c> the LIKE has no escape character
    /// (the <c>_</c> in names would be a wildcard), so the name is compared with <c>WHERE Name =</c>.
    /// </summary>
    public string EstimatedCount(DbObjectRef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return NoBackslashEscapes
            ? $"SHOW TABLE STATUS FROM {QuoteIdentifier(table.Schema)} WHERE `Name` = {StringLiteral(table.Name)}"
            : $"SHOW TABLE STATUS FROM {QuoteIdentifier(table.Schema)} LIKE {PlainStringLiteral(EscapeLike(table.Name))}";
    }

    /// <summary>Escapes <c>%</c>, <c>_</c> and <c>\</c> for a literal LIKE pattern.</summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}

/// <summary>Capabilities per version (section 14.3; appendix A §12, HeidiSQL's <c>fr*</c> flags).</summary>
public sealed class MySqlCapabilities(ServerIdentity server) : ICapabilities
{
    public char IdentifierQuote => '`';

    public IReadOnlyList<string> ExplainFormats =>
        server.Engine == DbEngine.MySql && server.Version >= new Version(5, 6, 5) ? ["TRADITIONAL", "JSON"] : ["TRADITIONAL"];

    public bool IsMariaDb => server.Engine == DbEngine.MariaDb;

    public bool Supports(Capability capability)
    {
        var v = server.Version;
        var maria = IsMariaDb;
        return capability switch
        {
            Capability.RowValueComparison => true,
            Capability.KillQuery => v >= new Version(5, 0),
            Capability.TransactionalDdl => false,
            Capability.InvisibleColumns => maria ? v >= new Version(10, 3, 3) : v >= new Version(8, 0, 23),
            Capability.CheckConstraintsEnforced => maria ? v >= new Version(10, 2, 1) : v >= new Version(8, 0, 16),
            Capability.Returning => maria && v >= new Version(10, 5),
            Capability.Sequences => maria && v >= new Version(10, 3),
            Capability.SystemVersionedTables => maria && v >= new Version(10, 3, 4),
            Capability.Events => v >= new Version(5, 1),
            Capability.IndexVisibility => maria ? v >= new Version(10, 6) : v >= new Version(8, 0),
            Capability.ProfileQuery => true,
            Capability.CreateOrReplaceObjects => maria && v >= new Version(10, 1, 4),
            _ => false,
        };
    }

    /// <summary>Fractional seconds in temporal types (MariaDB ≥ 5.3, MySQL ≥ 5.6.4).</summary>
    public bool TemporalFractions => IsMariaDb ? server.Version >= new Version(5, 3) : server.Version >= new Version(5, 6, 4);

    /// <summary><c>information_schema.CHECK_CONSTRAINTS</c> exists.</summary>
    public bool HasCheckConstraintsTable => IsMariaDb ? server.Version >= new Version(10, 2, 22) : server.Version >= new Version(8, 0, 16);

    /// <summary>
    /// MySQL 8's <c>information_schema</c> (data dictionary) shows expressions escaped by <c>String::print</c>:
    /// <c>GENERATION_EXPRESSION</c>, the <c>COLUMN_DEFAULT</c> of expression defaults and <c>CHECK_CLAUSE</c>
    /// (MySQL bug #100607; <c>SHOW CREATE TABLE</c> has them right). MySQL 5.7 and MariaDB return them as they are.
    /// </summary>
    public bool EscapesDictionaryExpressions => server.Engine == DbEngine.MySql && server.Version >= new Version(8, 0);
}

/// <summary>MySQL/MariaDB error classification (section 12.5): decides reconnect, retry and messages.</summary>
public sealed class MySqlErrorClassifier : IErrorClassifier
{
    public ClassifiedError Classify(DbError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code switch
        {
            // CR_SERVER_GONE_ERROR, CR_SERVER_LOST, CR_CONNECTION_ERROR, CR_CONN_HOST_ERROR, ER_CONNECTION_KILLED, CR_SERVER_LOST_EXTENDED.
            2006 or 2013 or 2002 or 2003 or 1042 or 1159 or 1160 or 1161 or 1927 or 2055 or 1053 => new ClassifiedError(DbErrorCategory.ConnectionLost, true, true, "The connection to the server was lost."),
            1213 => new ClassifiedError(DbErrorCategory.Deadlock, false, true, "Deadlock found; the transaction was rolled back and can be retried."),
            1205 => new ClassifiedError(DbErrorCategory.LockTimeout, false, true, "Lock wait timeout exceeded."),
            1064 or 1149 => new ClassifiedError(DbErrorCategory.Syntax, false, false, null),
            1044 or 1045 or 1142 or 1143 or 1227 or 1370 => new ClassifiedError(DbErrorCategory.Permission, false, false, null),
            1317 => new ClassifiedError(DbErrorCategory.Cancelled, false, false, "Query execution was interrupted."),
            3024 or 1969 => new ClassifiedError(DbErrorCategory.Timeout, false, true, "Query execution was interrupted, maximum statement execution time exceeded."),
            1062 or 1451 or 1452 or 1048 or 3819 or 4025 => new ClassifiedError(DbErrorCategory.Constraint, false, false, null),
            _ => new ClassifiedError(DbErrorCategory.Other, false, false, null),
        };
    }
}
