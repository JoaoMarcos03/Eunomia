using System.Collections.Frozen;

namespace Eunomia.Engine.Sql.Lexing;

/// <summary>
/// Listas estáticas do spike: keywords, tipos e funções MySQL mais comuns, escritas a partir da
/// documentação do MySQL (não copiadas do HeidiSQL). No produto vêm de <c>mysql.help_topic</c> (secção 16.1).
/// </summary>
public static class MySqlKeywords
{
    public static readonly FrozenSet<string> Keywords = Create(
        "ACTION", "ADD", "AFTER", "AGAINST", "ALGORITHM", "ALL", "ALTER", "ANALYZE", "AND", "ANY", "AS", "ASC", "AT",
        "AUTO_INCREMENT", "BEFORE", "BEGIN", "BETWEEN", "BOTH", "BTREE", "BY", "CALL", "CASCADE", "CASCADED", "CASE",
        "CHAIN", "CHANGE", "CHARACTER", "CHARSET", "CHECK", "CHECKSUM", "CLOSE", "COLLATE", "COLUMN", "COLUMNS",
        "COMMIT", "COMMITTED", "COMPRESSED", "CONCURRENT", "CONDITION", "CONSTRAINT", "CONTAINS", "CONTINUE",
        "CONVERT", "CREATE", "CROSS", "CURSOR", "DATABASE", "DATABASES", "DEALLOCATE", "DECLARE", "DEFAULT", "DEFINER",
        "DELAYED", "DELETE", "DELIMITER", "DESC", "DESCRIBE", "DETERMINISTIC", "DISABLE", "DISTINCT", "DISTINCTROW",
        "DIV", "DO", "DROP", "DUAL", "DUPLICATE", "EACH", "ELSE", "ELSEIF", "ENABLE", "ENCLOSED", "END", "ENDS",
        "ENGINE", "ENGINES", "ESCAPE", "ESCAPED", "EVENT", "EVERY", "EXCEPT", "EXECUTE", "EXISTS", "EXIT", "EXPLAIN",
        "EXTENDED", "FALSE", "FETCH", "FIRST", "FLUSH", "FOLLOWING", "FOR", "FORCE", "FOREIGN", "FROM", "FULLTEXT",
        "FUNCTION", "GENERATED", "GLOBAL", "GRANT", "GRANTS", "GROUP", "HANDLER", "HAVING", "HIGH_PRIORITY",
        "IDENTIFIED", "IF", "IGNORE", "IN", "INDEX", "INFILE", "INNER", "INOUT", "INSERT", "INSTALL", "INTERSECT",
        "INTERVAL", "INTO", "INVISIBLE", "INVOKER", "IS", "ISOLATION", "ITERATE", "JOIN", "KEY", "KEYS", "KILL",
        "LANGUAGE", "LATERAL", "LEADING", "LEAVE", "LEFT", "LIKE", "LIMIT", "LINES", "LOAD", "LOCAL", "LOCK", "LOOP",
        "LOW_PRIORITY", "MATCH", "MAXVALUE", "MERGE", "MOD", "MODIFY", "NAMES", "NATURAL", "NO", "NOT", "NULL", "OF",
        "OFFSET", "ON", "OPEN", "OPTIMIZE", "OPTION", "OPTIONALLY", "OR", "ORDER", "OUT", "OUTER", "OUTFILE", "OVER",
        "PARTITION", "PARTITIONS", "PERSISTENT", "PLUGINS", "PRECEDING", "PREPARE", "PRESERVE", "PRIMARY",
        "PRIVILEGES", "PROCEDURE", "PROCESSLIST", "PURGE", "RANGE", "READ", "READS", "RECURSIVE", "REFERENCES",
        "REGEXP", "RELEASE", "RENAME", "REPAIR", "REPEAT", "REPEATABLE", "REPLACE", "REQUIRE", "RESIGNAL", "RESTRICT",
        "RETURN", "RETURNING", "RETURNS", "REVOKE", "RIGHT", "RLIKE", "ROLLBACK", "ROLLUP", "ROUTINE", "ROWS",
        "SAVEPOINT", "SCHEDULE", "SCHEMA", "SCHEMAS", "SECURITY", "SELECT", "SEPARATOR", "SERIALIZABLE", "SESSION",
        "SET", "SHOW", "SHUTDOWN", "SIGNAL", "SPATIAL", "SQLEXCEPTION", "SQLSTATE", "SQLWARNING",
        "SQL_CALC_FOUND_ROWS", "START", "STARTING", "STARTS", "STATUS", "STORED", "STRAIGHT_JOIN", "TABLE", "TABLES",
        "TEMPORARY", "TEMPTABLE", "TERMINATED", "THEN", "TO", "TRAILING", "TRANSACTION", "TRIGGER", "TRIGGERS", "TRUE",
        "TRUNCATE", "UNCOMMITTED", "UNDEFINED", "UNINSTALL", "UNION", "UNIQUE", "UNLOCK", "UNSIGNED", "UNTIL",
        "UPDATE", "USE", "USING", "VALUES", "VARIABLES", "VIEW", "VIRTUAL", "VISIBLE", "WARNINGS", "WHEN", "WHERE",
        "WHILE", "WINDOW", "WITH", "WITHOUT", "WRITE", "XOR", "ZEROFILL");

    public static readonly FrozenSet<string> DataTypes = Create(
        "BIGINT", "BINARY", "BIT", "BLOB", "BOOL", "BOOLEAN", "CHAR", "DATE", "DATETIME", "DECIMAL", "DOUBLE",
        "ENUM", "FLOAT", "GEOMETRY", "INT", "INTEGER", "JSON", "LINESTRING", "LONGBLOB", "LONGTEXT", "MEDIUMBLOB",
        "MEDIUMINT", "MEDIUMTEXT", "NUMERIC", "POINT", "POLYGON", "REAL", "SMALLINT", "TEXT", "TIME", "TIMESTAMP",
        "TINYBLOB", "TINYINT", "TINYTEXT", "VARBINARY", "VARCHAR", "YEAR");

    public static readonly FrozenSet<string> Functions = Create(
        "ABS", "AVG", "CAST", "CEIL", "CEILING", "CHAR_LENGTH", "COALESCE", "CONCAT", "CONCAT_WS", "CONVERT",
        "COUNT", "CURDATE", "CURTIME", "DATEDIFF", "DATE_ADD", "DATE_FORMAT", "DATE_SUB", "FLOOR", "FROM_UNIXTIME",
        "GROUP_CONCAT", "IFNULL", "JSON_ARRAY", "JSON_EXTRACT", "JSON_OBJECT", "LENGTH", "LOWER", "LTRIM", "MAX",
        "MD5", "MIN", "NOW", "NULLIF", "ROUND", "RTRIM", "SHA1", "SHA2", "SUBSTR", "SUBSTRING", "SUM", "TRIM",
        "UNIX_TIMESTAMP", "UPPER", "UUID");

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> KeywordLookup = Keywords.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> DataTypeLookup = DataTypes.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> FunctionLookup = Functions.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Classifica uma palavra sem alocar. Keywords têm prioridade sobre tipos e funções.</summary>
    public static SqlTokenKind Classify(ReadOnlySpan<char> word)
    {
        if (KeywordLookup.Contains(word))
        {
            return SqlTokenKind.Keyword;
        }
        if (DataTypeLookup.Contains(word))
        {
            return SqlTokenKind.DataType;
        }
        return FunctionLookup.Contains(word) ? SqlTokenKind.Function : SqlTokenKind.Identifier;
    }

    private static FrozenSet<string> Create(params string[] words) => words.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
