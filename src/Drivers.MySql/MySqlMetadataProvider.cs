using System.Globalization;
using Eunomia.Drivers.Abstractions;
using MySqlConnector;

namespace Eunomia.Drivers.MySql;

/// <summary>
/// MySQL/MariaDB catalog (section 14.1, appendix A §11). A database's objects come in a single round trip
/// (tables and views, routines, triggers and events in a multi-statement, #1672). Foreign keys with two queries
/// joined on the client, like HeidiSQL (#852).
/// </summary>
public sealed class MySqlMetadataProvider(MySqlConnectionHandle handle) : IMetadataProvider
{
    private static readonly MySqlTypeMapper Types = new();

    private MySqlDialect Dialect => handle.Dialect;

    private MySqlCapabilities Capabilities => new(handle.Server);

    public async Task<IReadOnlyList<string>> GetCatalogsAsync(CancellationToken cancellationToken)
    {
        var rows = await handle.QueryRowsAsync("SHOW DATABASES", cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r[0]!).ToList();
    }

    public Task<IReadOnlyList<string>> GetSchemasAsync(string catalog, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([catalog]);

    public async Task<IReadOnlyList<DbObjectInfo>> GetObjectsAsync(string catalog, string schema, ObjectDetail detail, CancellationToken cancellationToken)
    {
        var db = Dialect.StringLiteral(schema);
        var tables = detail switch
        {
            ObjectDetail.Statistics => "SHOW TABLE STATUS FROM " + Dialect.QuoteIdentifier(schema),
            // Only name and kind: on servers with a file per table, the other columns open every table (PR-08).
            ObjectDetail.Names => "SELECT TABLE_NAME AS `Name`, IF(TABLE_TYPE = 'VIEW', 'VIEW', NULL) AS `Comment` FROM information_schema.TABLES WHERE TABLE_SCHEMA = " + db,
            _ => "SELECT TABLE_NAME AS `Name`, ENGINE AS `Engine`, VERSION AS `Version`, ROW_FORMAT AS `Row_format`, NULL AS `Rows`, " +
                 "NULL AS `Avg_row_length`, NULL AS `Data_length`, NULL AS `Max_data_length`, NULL AS `Index_length`, NULL AS `Data_free`, " +
                 "AUTO_INCREMENT AS `Auto_increment`, CREATE_TIME AS `Create_time`, UPDATE_TIME AS `Update_time`, CHECK_TIME AS `Check_time`, " +
                 "TABLE_COLLATION AS `Collation`, CHECKSUM AS `Checksum`, CREATE_OPTIONS AS `Create_options`, " +
                 "IF(TABLE_TYPE = 'VIEW', 'VIEW', TABLE_COMMENT) AS `Comment` FROM information_schema.TABLES WHERE TABLE_SCHEMA = " + db,
        };
        var sql = tables + ";\n" +
                  "SELECT ROUTINE_NAME, ROUTINE_TYPE, ROUTINE_COMMENT, CREATED, LAST_ALTERED FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = " + db + ";\n" +
                  // SHOW TRIGGERS only opens the requested database; MariaDB 10.x's information_schema.TRIGGERS went through the
                  // tables of every database (590 ms against 33 ms for a 32-table database, with other large databases).
                  "SHOW TRIGGERS FROM " + Dialect.QuoteIdentifier(schema);
        var withEvents = Capabilities.Supports(Capability.Events);
        if (withEvents)
        {
            sql += ";\nSELECT EVENT_NAME, EVENT_COMMENT, CREATED, LAST_ALTERED FROM information_schema.EVENTS WHERE EVENT_SCHEMA = " + db;
        }

        var result = new List<DbObjectInfo>();
        await using var cmd = handle.CreateCommand(sql);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            // 1. Tables and views.
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var status = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    status[reader.GetName(i)] = reader.IsDBNull(i) ? null : MySqlConnectionHandle.AsText(reader, i);
                }
                var name = status["Name"]!;
                var comment = status.GetValueOrDefault("Comment");
                if (detail == ObjectDetail.Names)
                {
                    var kind = string.Equals(comment, "VIEW", StringComparison.Ordinal) ? DbObjectKind.View : DbObjectKind.Table;
                    result.Add(new DbObjectInfo(new DbObjectRef(catalog, schema, name, kind)));
                    continue;
                }
                var isView = string.Equals(comment, "VIEW", StringComparison.Ordinal) && status.GetValueOrDefault("Engine") is null;
                result.Add(new DbObjectInfo(
                    new DbObjectRef(catalog, schema, name, isView ? DbObjectKind.View : DbObjectKind.Table),
                    DataBytes: Long(status.GetValueOrDefault("Data_length")),
                    IndexBytes: Long(status.GetValueOrDefault("Index_length")),
                    RowsEstimate: Long(status.GetValueOrDefault("Rows")),
                    Engine: status.GetValueOrDefault("Engine"),
                    Comment: isView ? null : comment,
                    Updated: Date(status.GetValueOrDefault("Update_time")))
                {
                    Created = Date(status.GetValueOrDefault("Create_time")),
                    Status = status,
                });
            }
            // 2. Procedures and functions.
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var kind = string.Equals(reader.GetString(1), "FUNCTION", StringComparison.OrdinalIgnoreCase) ? DbObjectKind.Function : DbObjectKind.Procedure;
                result.Add(new DbObjectInfo(new DbObjectRef(catalog, schema, reader.GetString(0), kind),
                    Comment: reader.IsDBNull(2) ? null : reader.GetString(2),
                    Updated: reader.IsDBNull(4) ? null : reader.GetDateTime(4))
                {
                    Created = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                });
            }
            // 3. Triggers (SHOW TRIGGERS: "Trigger" and, on servers that have it, "Created").
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            var created = -1;
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (string.Equals(reader.GetName(i), "Created", StringComparison.OrdinalIgnoreCase))
                {
                    created = i;
                }
            }
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new DbObjectInfo(new DbObjectRef(catalog, schema, reader.GetString(0), DbObjectKind.Trigger))
                {
                    Created = created < 0 || reader.IsDBNull(created) ? null : reader.GetDateTime(created),
                });
            }
            // 4. Events.
            if (withEvents && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    result.Add(new DbObjectInfo(new DbObjectRef(catalog, schema, reader.GetString(0), DbObjectKind.Event),
                        Comment: reader.IsDBNull(1) ? null : reader.GetString(1),
                        Updated: reader.IsDBNull(3) ? null : reader.GetDateTime(3))
                    {
                        Created = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                    });
                }
            }
        }
        finally
        {
            handle.Finished(cmd);
        }
        return result;
    }

    /// <summary>
    /// Columns, keys, FKs, checks and options of a table or view in a single round trip (there used to be six, in series:
    /// on a remote server each one cost the full latency).
    /// </summary>
    public async Task<TableDefinition> GetTableAsync(DbObjectRef table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        List<List<string?[]>> sets;
        try
        {
            sets = await handle.QueryResultSetsAsync(TableQueries(table, generation: true), cancellationToken).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.BadFieldError)
        {
            // Servers without generated columns (MySQL < 5.7, MariaDB < 10.2): no GENERATION_EXPRESSION.
            sets = await handle.QueryResultSetsAsync(TableQueries(table, generation: false), cancellationToken).ConfigureAwait(false);
        }
        var checks = Capabilities.HasCheckConstraintsTable;
        var (columnRows, keyRows, rc, kcu, checkRows, statusRows) = (sets[0], sets[1], sets[2], sets[3], checks ? sets[4] : [], sets[checks ? 5 : 4]);

        var columns = new List<ColumnDefinition>();
        foreach (var r in columnRows)
        {
            var extra = r[4] ?? string.Empty;
            var nullable = string.Equals(r[2], "YES", StringComparison.OrdinalIgnoreCase);
            var type = Types.Map(r[1]!) with { Charset = r[6], Collation = r[7] };
            var generation = r.Length > 8 && !string.IsNullOrEmpty(r[8]) ? DictionaryExpression(r[8]!) : null;
            // "DEFAULT_GENERATED" (MySQL 8) is a default with an expression, not a generated column.
            var generated = generation is not null || IsGeneratedExtra(extra);
            var (defaultSql, defaultIsExpression) = generated ? (null, false) : CanonicalDefault(r[3], extra, nullable, type);
            columns.Add(new ColumnDefinition(
                r[0]!,
                r[1]!,
                type,
                nullable,
                defaultSql,
                extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                generated,
                extra.Contains("INVISIBLE", StringComparison.OrdinalIgnoreCase),
                string.IsNullOrEmpty(r[5]) ? null : r[5])
            {
                DefaultIsExpression = defaultIsExpression,
                OnUpdate = OnUpdateOf(extra),
                GenerationExpression = generation,
                IsStored = extra.Contains("STORED", StringComparison.OrdinalIgnoreCase) || extra.Contains("PERSISTENT", StringComparison.OrdinalIgnoreCase),
                OriginalName = r[0],
            });
        }

        var indexes = new List<IndexDefinition>();
        foreach (var group in keyRows.GroupBy(r => r[2]!, StringComparer.Ordinal))
        {
            var first = group.First();
            var kind = group.Key == "PRIMARY" ? IndexKind.Primary
                : string.Equals(first[10], "FULLTEXT", StringComparison.OrdinalIgnoreCase) ? IndexKind.FullText
                : string.Equals(first[10], "SPATIAL", StringComparison.OrdinalIgnoreCase) ? IndexKind.Spatial
                : first[1] == "0" ? IndexKind.Unique : IndexKind.Key;
            var parts = group.OrderBy(r => int.Parse(r[3]!, CultureInfo.InvariantCulture)).ToList();
            var cols = parts.Select(r => r[4] ?? string.Empty).ToList();
            var subParts = parts.Select(r => int.TryParse(r[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null).ToList();
            indexes.Add(new IndexDefinition(group.Key, kind, cols)
            {
                SubParts = subParts.Any(p => p is not null) ? subParts : null,
                Algorithm = string.Equals(first[10], "HASH", StringComparison.OrdinalIgnoreCase) ? "HASH" : null,
                Comment = first.Length > 12 && !string.IsNullOrEmpty(first[12]) ? first[12] : null,
            });
        }

        var fks = new List<ForeignKeyDefinition>();
        foreach (var c in rc)
        {
            var parts = kcu.Where(k => k[0] == c[0]).ToList();
            if (parts.Count == 0)
            {
                continue;
            }
            var refSchema = parts[0][2] ?? table.Schema;
            fks.Add(new ForeignKeyDefinition(
                c[0]!,
                parts.Select(p => p[1]!).ToList(),
                new DbObjectRef(refSchema, refSchema, parts[0][3]!, DbObjectKind.Table),
                parts.Select(p => p[4]!).ToList(),
                Action(c[1]),
                Action(c[2])));
        }

        var checkDefinitions = checkRows.Select(r => new CheckDefinition(r[0]!, DictionaryExpression(r[1]!))).ToList();

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (statusRows.Count > 0)
        {
            string[] keys = ["Engine", "Collation", "Comment", "Auto_increment", "Row_format", "Create_options"];
            for (var i = 0; i < keys.Length; i++)
            {
                if (statusRows[0][i] is { } v)
                {
                    options[keys[i]] = v;
                }
            }
        }
        return new TableDefinition(table, columns, indexes, fks, checkDefinitions, options);
    }

    /// <summary>The queries of <see cref="GetTableAsync"/>, in result set order.</summary>
    private string TableQueries(DbObjectRef table, bool generation)
    {
        var db = Dialect.StringLiteral(table.Schema);
        var name = Dialect.StringLiteral(table.Name);
        var sql = new System.Text.StringBuilder();
        sql.Append("SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA, COLUMN_COMMENT, CHARACTER_SET_NAME, COLLATION_NAME")
            .Append(generation ? ", GENERATION_EXPRESSION" : string.Empty)
            .Append(CultureInfo.InvariantCulture, $" FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = {db} AND TABLE_NAME = {name} ORDER BY ORDINAL_POSITION;\n");
        sql.Append(CultureInfo.InvariantCulture, $"SHOW KEYS FROM {Dialect.QuoteIdentifier(table.Name)} FROM {Dialect.QuoteIdentifier(table.Schema)};\n");
        sql.Append("SELECT CONSTRAINT_NAME, UPDATE_RULE, DELETE_RULE, REFERENCED_TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS ")
            .Append(CultureInfo.InvariantCulture, $"WHERE CONSTRAINT_SCHEMA = {db} AND TABLE_NAME = {name};\n");
        sql.Append("SELECT CONSTRAINT_NAME, COLUMN_NAME, REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE ")
            .Append(CultureInfo.InvariantCulture, $"WHERE TABLE_SCHEMA = {db} AND TABLE_NAME = {name} AND REFERENCED_TABLE_NAME IS NOT NULL ORDER BY CONSTRAINT_NAME, ORDINAL_POSITION;\n");
        if (Capabilities.HasCheckConstraintsTable)
        {
            // MariaDB has TABLE_NAME in CHECK_CONSTRAINTS: the direct query filters by database and table. A JOIN with
            // TABLE_CONSTRAINTS opened the tables of every database (seconds with thousands of tables on the server).
            sql.Append(Capabilities.IsMariaDb
                ? $"SELECT CONSTRAINT_NAME, CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = {db} AND TABLE_NAME = {name};\n"
                : "SELECT tc.CONSTRAINT_NAME, cc.CHECK_CLAUSE FROM information_schema.TABLE_CONSTRAINTS tc JOIN information_schema.CHECK_CONSTRAINTS cc " +
                  "ON cc.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA AND cc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME " +
                  $"WHERE tc.TABLE_SCHEMA = {db} AND tc.TABLE_NAME = {name} AND tc.CONSTRAINT_TYPE = 'CHECK' AND cc.CONSTRAINT_SCHEMA = {db};\n");
        }
        sql.Append("SELECT ENGINE, TABLE_COLLATION, TABLE_COMMENT, AUTO_INCREMENT, ROW_FORMAT, CREATE_OPTIONS FROM information_schema.TABLES ")
            .Append(CultureInfo.InvariantCulture, $"WHERE TABLE_SCHEMA = {db} AND TABLE_NAME = {name}");
        return sql.ToString();
    }

    /// <summary>
    /// <c>COLUMN_DEFAULT</c> as the SQL after <c>DEFAULT</c>, the same on both engines: MariaDB (10.2.7+) already gives it as SQL
    /// (<c>'abc'</c>, <c>NULL</c>, <c>current_timestamp()</c>); MySQL gives the text unquoted and flags expressions with
    /// <c>DEFAULT_GENERATED</c>.
    /// </summary>
    internal (string? Sql, bool IsExpression) CanonicalDefault(string? value, string extra, bool nullable, LogicalType type)
    {
        var mariaSql = handle.Server.Engine == DbEngine.MariaDb && handle.Server.Version >= new Version(10, 2, 7);
        if (value is null)
        {
            return nullable && !mariaSql && !extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) ? ("NULL", false) : (null, false);
        }
        if (mariaSql)
        {
            if (string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase))
            {
                return ("NULL", false);
            }
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                // The server writes the literal with backslash escapes; it is quoted again so it holds under any sql_mode.
                return (Dialect.StringLiteral(UnquoteServerLiteral(value)), false);
            }
            var literal = decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            return (value, !literal);
        }
        if (extra.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase))
        {
            var expression = DictionaryExpression(value);
            return (IsFunctionLike(expression) ? expression : "(" + expression + ")", true);
        }
        if (string.Equals(value, "CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase) && type.Kind is LogicalTypeKind.DateTime)
        {
            return (value, true);
        }
        var numeric = type.Kind is LogicalTypeKind.Integer or LogicalTypeKind.Decimal or LogicalTypeKind.Float or LogicalTypeKind.Boolean
                      && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        return (numeric ? value : Dialect.StringLiteral(value), false);
    }

    /// <summary>Text of a literal written by the server (<c>append_unescaped</c>): removes the quotes and undoes <c>''</c> and the backslashes.</summary>
    internal static string UnquoteServerLiteral(string literal)
    {
        var inner = literal.AsSpan(1, literal.Length - 2);
        var sb = new System.Text.StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                i++;
                sb.Append(inner[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', 'Z' => '\u001A', 'b' => '\b', var x => x });
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

    /// <summary>An <c>information_schema</c> expression as the server stored it (<see cref="MySqlCapabilities.EscapesDictionaryExpressions"/>).</summary>
    private string DictionaryExpression(string text) => Capabilities.EscapesDictionaryExpressions ? UnescapeDictionaryText(text) : text;

    /// <summary>
    /// Undoes MySQL 8's <c>String::print</c>: <c>\\</c>, <c>\0</c>, <c>\'</c>, <c>\n</c>, <c>\r</c> and <c>\Z</c>. That escape
    /// always puts a backslash before every quote and every backslash, so a lone quote, or a backslash followed by anything
    /// else, means the text is not escaped (a server with the bug fixed) and it is left as it is.
    /// </summary>
    internal static string UnescapeDictionaryText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                return text;
            }
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            char? original = i + 1 < text.Length
                ? text[i + 1] switch { '\\' => '\\', '0' => '\0', '\'' => '\'', 'n' => '\n', 'r' => '\r', 'Z' => '\u001A', _ => null }
                : null;
            if (original is null)
            {
                return text;
            }
            sb.Append(original.Value);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsFunctionLike(string value) =>
        value.StartsWith("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase) || value.StartsWith("NOW(", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("LOCALTIME", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>VIRTUAL GENERATED</c>, <c>STORED GENERATED</c> (MySQL), <c>VIRTUAL</c>, <c>PERSISTENT</c> or <c>STORED</c> (MariaDB).</summary>
    internal static bool IsGeneratedExtra(string extra) =>
        extra.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.ToUpperInvariant() is "VIRTUAL" or "STORED" or "PERSISTENT");

    /// <summary><c>on update CURRENT_TIMESTAMP</c> from <c>EXTRA</c>.</summary>
    private static string? OnUpdateOf(string extra)
    {
        var at = extra.IndexOf("on update ", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : extra[(at + "on update ".Length)..].Trim();
    }

    public async Task<string> GetCreateCodeAsync(DbObjectRef obj, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var (keyword, column) = obj.Kind switch
        {
            DbObjectKind.View => ("VIEW", 1),
            DbObjectKind.Procedure => ("PROCEDURE", 2),
            DbObjectKind.Function => ("FUNCTION", 2),
            DbObjectKind.Trigger => ("TRIGGER", 2),
            DbObjectKind.Event => ("EVENT", 3),
            _ => ("TABLE", 1),
        };
        var rows = await handle.QueryRowsAsync($"SHOW CREATE {keyword} {Dialect.QualifiedName(obj)}", cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 && rows[0].Length > column ? rows[0][column] ?? string.Empty : string.Empty;
    }

    public async Task<IReadOnlyList<TableDefinition>> GetSchemaAsync(string catalog, string schema, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var db = Dialect.StringLiteral(schema);
        var tables = await handle.QueryRowsAsync(
            $"SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = {db} AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME", cancellationToken).ConfigureAwait(false);
        var columns = await handle.QueryRowsAsync(
            "SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, EXTRA FROM information_schema.COLUMNS " +
            $"WHERE TABLE_SCHEMA = {db} ORDER BY TABLE_NAME, ORDINAL_POSITION", cancellationToken).ConfigureAwait(false);
        var keys = await handle.QueryRowsAsync(
            "SELECT TABLE_NAME, INDEX_NAME, COLUMN_NAME FROM information_schema.STATISTICS " +
            $"WHERE TABLE_SCHEMA = {db} AND NON_UNIQUE = 0 ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX", cancellationToken).ConfigureAwait(false);
        var fks = await handle.QueryRowsAsync(
            "SELECT TABLE_NAME, CONSTRAINT_NAME, COLUMN_NAME, REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE " +
            $"WHERE TABLE_SCHEMA = {db} AND REFERENCED_TABLE_NAME IS NOT NULL ORDER BY TABLE_NAME, CONSTRAINT_NAME, ORDINAL_POSITION", cancellationToken).ConfigureAwait(false);

        var columnsByTable = columns.GroupBy(r => r[0]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var keysByTable = keys.GroupBy(r => r[0]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var fksByTable = fks.GroupBy(r => r[0]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var result = new List<TableDefinition>(tables.Count);
        foreach (var name in tables.Select(t => t[0]!))
        {
            var columnList = (columnsByTable.GetValueOrDefault(name) ?? []).Select(r =>
            {
                var extra = r[4] ?? string.Empty;
                return new ColumnDefinition(r[1]!, r[2]!, Types.Map(r[2]!), string.Equals(r[3], "YES", StringComparison.OrdinalIgnoreCase), null,
                    extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase), IsGeneratedExtra(extra), extra.Contains("INVISIBLE", StringComparison.OrdinalIgnoreCase), null)
                {
                    OriginalName = r[1],
                };
            }).ToList();
            var indexList = (keysByTable.GetValueOrDefault(name) ?? []).GroupBy(r => r[1]!, StringComparer.Ordinal)
                .Select(g => new IndexDefinition(g.Key, g.Key == "PRIMARY" ? IndexKind.Primary : IndexKind.Unique, g.Select(r => r[2] ?? string.Empty).ToList()))
                .ToList();
            var fkList = (fksByTable.GetValueOrDefault(name) ?? []).GroupBy(r => r[1]!, StringComparer.Ordinal)
                .Select(g =>
                {
                    var first = g.First();
                    var refSchema = first[3] ?? schema;
                    return new ForeignKeyDefinition(g.Key, g.Select(r => r[2]!).ToList(), new DbObjectRef(refSchema, refSchema, first[4]!, DbObjectKind.Table),
                        g.Select(r => r[5]!).ToList(), ForeignKeyAction.Restrict, ForeignKeyAction.Restrict);
                })
                .ToList();
            result.Add(new TableDefinition(new DbObjectRef(catalog, schema, name, DbObjectKind.Table), columnList, indexList, fkList, [], new Dictionary<string, string>()));
        }
        return result;
    }

    public async Task<string> GetCreateDatabaseAsync(string database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await handle.QueryRowsAsync("SHOW CREATE DATABASE " + Dialect.QuoteIdentifier(database), cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 && rows[0].Length > 1 ? rows[0][1] ?? string.Empty : string.Empty;
    }

    public async Task<string> GetTriggerSqlModeAsync(DbObjectRef trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        // Columns: Trigger, sql_mode, SQL Original Statement, …
        var rows = await handle.QueryRowsAsync("SHOW CREATE TRIGGER " + Dialect.QualifiedName(trigger), cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 && rows[0].Length > 1 ? rows[0][1] ?? string.Empty : string.Empty;
    }

    public async Task<IReadOnlyList<ServerVariable>> GetVariablesAsync(bool session, CancellationToken cancellationToken)
    {
        var rows = await handle.QueryRowsAsync(session ? "SHOW SESSION VARIABLES" : "SHOW GLOBAL VARIABLES", cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new ServerVariable(r[0]!, r[1] ?? string.Empty, session)).ToList();
    }

    public async Task<SqlHelp> GetHelpAsync(string topic, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topic);
        // HELP returns different shapes: (name, description, example) for a topic; (name, is_it_category) for
        // several; (source_category_name, name, is_it_category) for a category. The column names tell which one it is.
        await using var cmd = handle.CreateCommand("HELP " + Dialect.PlainStringLiteral(topic));
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            int Column(string name) => columns.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            string? Text(int ordinal) => ordinal < 0 || reader.IsDBNull(ordinal) ? null : MySqlConnectionHandle.AsText(reader, ordinal);
            var description = Column("description");
            if (description >= 0)
            {
                return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? new SqlHelp(Text(Column("name")) ?? topic, Text(description), Text(Column("example")), [])
                    : new SqlHelp(null, null, null, []);
            }
            var name = Column("name");
            var category = Column("is_it_category");
            var entries = new List<SqlHelpEntry>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Text(name) is { } entry)
                {
                    entries.Add(new SqlHelpEntry(entry, string.Equals(Text(category), "Y", StringComparison.OrdinalIgnoreCase)));
                }
            }
            return new SqlHelp(null, null, null, entries);
        }
        finally
        {
            handle.Finished(cmd);
        }
    }

    public async Task<IReadOnlyList<ServerVariable>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var rows = await handle.QueryRowsAsync("SHOW GLOBAL STATUS", cancellationToken).ConfigureAwait(false);
        return rows.Where(r => r[0] is not null).Select(r => new ServerVariable(r[0]!, r[1] ?? string.Empty, false)).ToList();
    }

    public async Task<IReadOnlyList<ServerProcess>> GetProcessesAsync(CancellationToken cancellationToken)
    {
        List<string?[]> rows;
        try
        {
            rows = await handle.QueryRowsAsync(
                "SELECT ID, USER, HOST, DB, COMMAND, TIME, STATE, LEFT(INFO, 51200) FROM information_schema.PROCESSLIST", cancellationToken).ConfigureAwait(false);
        }
        catch (MySqlException)
        {
            rows = await handle.QueryRowsAsync("SHOW FULL PROCESSLIST", cancellationToken).ConfigureAwait(false);
        }
        return rows.Select(r => new ServerProcess(
            long.Parse(r[0]!, CultureInfo.InvariantCulture),
            r[1] ?? string.Empty,
            r[2] ?? string.Empty,
            r[3],
            r[4] ?? string.Empty,
            TimeSpan.FromSeconds(double.TryParse(r[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0),
            r[6],
            r[7])).ToList();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetCatalogCollationsAsync(CancellationToken cancellationToken)
    {
        var rows = await handle.QueryRowsAsync("SELECT SCHEMA_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA", cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (r[0] is { } name && r[1] is { } collation)
            {
                result[name] = collation;
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<CollationInfo>> GetCollationsAsync(CancellationToken cancellationToken)
    {
        // Columns: Collation, Charset, Id, Default, Compiled, Sortlen (and more in recent versions).
        var rows = await handle.QueryRowsAsync("SHOW COLLATION", cancellationToken).ConfigureAwait(false);
        return rows
            .Where(r => r[0] is not null)
            .Select(r => new CollationInfo(r[0]!, r[1] ?? string.Empty, string.Equals(r[3], "Yes", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ForeignKeyAction Action(string? rule) => rule?.ToUpperInvariant() switch
    {
        "CASCADE" => ForeignKeyAction.Cascade,
        "SET NULL" => ForeignKeyAction.SetNull,
        "NO ACTION" => ForeignKeyAction.NoAction,
        "SET DEFAULT" => ForeignKeyAction.SetDefault,
        _ => ForeignKeyAction.Restrict,
    };

    private static long? Long(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTime? Date(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
