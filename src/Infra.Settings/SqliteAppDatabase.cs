using System.Globalization;
using Eunomia.App.Settings;
using Microsoft.Data.Sqlite;

namespace Eunomia.Infra.Settings;

/// <summary>
/// <c>app.db</c> (section 21.4) in SQLite: per-table state, recent filters, favourites and query history.
/// One shared connection protected by a lock (accesses are short); WAL so reads are not blocked.
/// The schema is versioned with <c>PRAGMA user_version</c>. A damaged file (power cut, full disk) is set aside
/// with the <c>.corrupt-&lt;date&gt;</c> suffix and the database starts empty, instead of the application not starting.
/// </summary>
public sealed class SqliteAppDatabase : IAppDatabase
{
    public const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    public SqliteAppDatabase(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            AtomicFile.CreatePrivateDirectory(directory);
        }
        try
        {
            _connection = OpenAndMigrate(path);
        }
        catch (SqliteException)
        {
            // A file that is not an SQLite database or is damaged: set aside (with the WAL) and a new one is started.
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            CorruptBackup = $"{path}.corrupt-{stamp}";
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                if (File.Exists(path + suffix))
                {
                    File.Move(path + suffix, CorruptBackup + suffix, overwrite: true);
                }
            }
            _connection = OpenAndMigrate(path);
        }
    }

    /// <summary><c>app.db</c> was damaged and was set aside at this path (favourites and history start over).</summary>
    public string? CorruptBackup { get; }

    private SqliteConnection OpenAndMigrate(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "PRAGMA quick_check";
                if (!string.Equals(cmd.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SqliteException("app.db failed the integrity check.", 11);
                }
            }
            Migrate(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public int CurrentSchemaVersion
    {
        get
        {
            lock (_gate)
            {
                return Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
            }
        }
    }

    private static void Migrate(SqliteConnection connection)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(read.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version < 1)
        {
            using var tx = connection.BeginTransaction();
            using var create = connection.CreateCommand();
            create.Transaction = tx;
            create.CommandText = """
                CREATE TABLE table_state (
                    session TEXT NOT NULL, db TEXT NOT NULL, tbl TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL,
                    PRIMARY KEY (session, db, tbl, name));
                CREATE TABLE recent_filters (
                    session TEXT NOT NULL, db TEXT NOT NULL, tbl TEXT NOT NULL, filter TEXT NOT NULL, used_at INTEGER NOT NULL,
                    PRIMARY KEY (session, db, tbl, filter));
                CREATE TABLE favorites (
                    session TEXT NOT NULL, path TEXT NOT NULL,
                    PRIMARY KEY (session, path));
                CREATE TABLE query_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, session TEXT NOT NULL, db TEXT NULL, sql TEXT NOT NULL,
                    executed_at INTEGER NOT NULL, duration_ms INTEGER NOT NULL);
                CREATE INDEX ix_query_history_session ON query_history (session, executed_at DESC);
                PRAGMA user_version = 1;
                """;
            create.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public string? GetTableState(TableKey table, string name)
    {
        lock (_gate)
        {
            return Scalar("SELECT value FROM table_state WHERE session=$s AND db=$d AND tbl=$t AND name=$n",
                ("$s", table.Session), ("$d", table.Database), ("$t", table.Table), ("$n", name)) as string;
        }
    }

    public void SetTableState(TableKey table, string name, string? value)
    {
        lock (_gate)
        {
            if (value is null)
            {
                Execute("DELETE FROM table_state WHERE session=$s AND db=$d AND tbl=$t AND name=$n", null,
                    ("$s", table.Session), ("$d", table.Database), ("$t", table.Table), ("$n", name));
            }
            else
            {
                Execute("INSERT INTO table_state (session, db, tbl, name, value) VALUES ($s, $d, $t, $n, $v) ON CONFLICT DO UPDATE SET value = excluded.value", null,
                    ("$s", table.Session), ("$d", table.Database), ("$t", table.Table), ("$n", name), ("$v", value));
            }
        }
    }

    public IReadOnlyList<string> GetRecentFilters(TableKey table)
    {
        lock (_gate)
        {
            return ReadStrings("SELECT filter FROM recent_filters WHERE session=$s AND db=$d AND tbl=$t ORDER BY used_at DESC, rowid DESC",
                ("$s", table.Session), ("$d", table.Database), ("$t", table.Table));
        }
    }

    public void AddRecentFilter(TableKey table, string filter, int max = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        if (string.IsNullOrWhiteSpace(filter))
        {
            return;
        }
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            // used_at strictly increasing: two entries in the same tick keep their insertion order.
            var next = Convert.ToInt64(Scalar("SELECT COALESCE(MAX(used_at), 0) + 1 FROM recent_filters", tx), CultureInfo.InvariantCulture);
            var now = Math.Max(next, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Execute("INSERT INTO recent_filters (session, db, tbl, filter, used_at) VALUES ($s, $d, $t, $f, $u) ON CONFLICT DO UPDATE SET used_at = excluded.used_at", tx,
                ("$s", table.Session), ("$d", table.Database), ("$t", table.Table), ("$f", filter), ("$u", now));
            Execute("""
                DELETE FROM recent_filters WHERE session=$s AND db=$d AND tbl=$t AND filter NOT IN (
                    SELECT filter FROM recent_filters WHERE session=$s AND db=$d AND tbl=$t ORDER BY used_at DESC LIMIT $m)
                """, tx, ("$s", table.Session), ("$d", table.Database), ("$t", table.Table), ("$m", max));
            tx.Commit();
        }
    }

    public void ClearRecentFilters(TableKey table)
    {
        lock (_gate)
        {
            Execute("DELETE FROM recent_filters WHERE session=$s AND db=$d AND tbl=$t", null,
                ("$s", table.Session), ("$d", table.Database), ("$t", table.Table));
        }
    }

    public IReadOnlyList<string> GetFavorites(string session)
    {
        lock (_gate)
        {
            return ReadStrings("SELECT path FROM favorites WHERE session=$s ORDER BY path", ("$s", session));
        }
    }

    public void SetFavorite(string session, string path, bool favorite)
    {
        lock (_gate)
        {
            if (favorite)
            {
                Execute("INSERT OR IGNORE INTO favorites (session, path) VALUES ($s, $p)", null, ("$s", session), ("$p", path));
            }
            else
            {
                Execute("DELETE FROM favorites WHERE session=$s AND path=$p", null, ("$s", session), ("$p", path));
            }
        }
    }

    public long AddHistory(string session, string? database, string sql, DateTimeOffset executedAt, TimeSpan duration)
    {
        lock (_gate)
        {
            return Convert.ToInt64(Scalar("""
                INSERT INTO query_history (session, db, sql, executed_at, duration_ms) VALUES ($s, $d, $q, $e, $u);
                SELECT last_insert_rowid();
                """, ("$s", session), ("$d", (object?)database ?? DBNull.Value), ("$q", sql), ("$e", executedAt.ToUnixTimeMilliseconds()), ("$u", (long)duration.TotalMilliseconds)),
                CultureInfo.InvariantCulture);
        }
    }

    public IReadOnlyList<QueryHistoryEntry> GetHistory(string session, int limit = 1000)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT id, session, db, sql, executed_at, duration_ms FROM query_history WHERE session=$s ORDER BY executed_at DESC, id DESC LIMIT $l", null,
                ("$s", session), ("$l", limit));
            using var reader = cmd.ExecuteReader();
            var result = new List<QueryHistoryEntry>();
            while (reader.Read())
            {
                result.Add(new QueryHistoryEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    TimeSpan.FromMilliseconds(reader.GetInt64(5))));
            }
            return result;
        }
    }

    public void DeleteHistory(long id)
    {
        lock (_gate)
        {
            Execute("DELETE FROM query_history WHERE id=$i", null, ("$i", id));
        }
    }

    public void ClearHistory(string session)
    {
        lock (_gate)
        {
            Execute("DELETE FROM query_history WHERE session=$s", null, ("$s", session));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx, params (string Name, object Value)[] parameters)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        return cmd;
    }

    private void Execute(string sql, SqliteTransaction? tx = null, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(sql, tx, parameters);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object Value)[] parameters) => Scalar(sql, null, parameters);

    private object? Scalar(string sql, SqliteTransaction? tx, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(sql, tx, parameters);
        return cmd.ExecuteScalar();
    }

    private List<string> ReadStrings(string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(sql, null, parameters);
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }
}
