using System.Text.RegularExpressions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;

namespace Eunomia.Engine.Metadata;

/// <summary>Session options that change the database list and the cost of the queries (appendix A §5, §6).</summary>
public sealed record MetadataOptions
{
    /// <summary>"Get full table status": sizes and rows in the background after the names.</summary>
    public bool FullTableStatus { get; init; } = true;

    /// <summary>Databases field: a literal list separated by <c>;</c> that replaces <c>SHOW DATABASES</c>.</summary>
    public string Databases { get; init; } = string.Empty;

    /// <summary>"Hide database pattern": regex that removes databases from the list.</summary>
    public string HideDatabasePattern { get; init; } = string.Empty;
}

/// <summary>Cached objects of a database and the level of detail already read (<see cref="HasStatistics"/>: sizes and rows are there).</summary>
public sealed record DatabaseObjects(string Database, IReadOnlyList<DbObjectInfo> Objects, ObjectDetail Detail, DateTimeOffset LoadedAt)
{
    public bool HasStatistics => Detail == ObjectDetail.Statistics;
}

public enum MetadataScope
{
    Databases,
    Objects,
    Table,
    All,
}

/// <summary>Metadata changed (loaded, statistics arrived, invalidated). Raised on any thread.</summary>
public sealed class MetadataChangedEventArgs(MetadataScope scope, string? database = null, string? objectName = null) : EventArgs
{
    public MetadataScope Scope { get; } = scope;

    public string? Database { get; } = database;

    public string? ObjectName { get; } = objectName;
}

/// <summary>
/// Metadata cache of a session (section 21.1 <c>Engine.Metadata</c>; 12.3; PR-08). Databases and objects are requested on the
/// metadata connection and stay cached until a refresh. Objects arrive in two phases: first only the names (a light query
/// on <c>information_schema</c>, so the tree shows right away) and, with "Get full table status", the statistics in
/// the background (low priority), which update the cache and raise <see cref="Changed"/>. Identical concurrent requests
/// share the same round trip. Thread-safe.
/// </summary>
public sealed class MetadataCache(SessionHost host, MetadataOptions options)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DatabaseObjects> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<DatabaseObjects>> _loadingObjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _loadingStatistics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObjectDetail> _loadingDetail = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingStatistics = new(StringComparer.Ordinal);
    private readonly Dictionary<DbObjectRef, TableDefinition> _tables = [];
    private IReadOnlyList<string>? _databases;
    private Task<IReadOnlyList<string>>? _loadingDatabases;
    private Task<IReadOnlyDictionary<string, string>>? _collations;
    private readonly Dictionary<string, Task> _serverLists = new(StringComparer.Ordinal);
    private int _generation;

    /// <summary>Goes up on every invalidation: a definition requested before does not enter the cache.</summary>
    private int _tableEpoch;

    /// <summary>
    /// Time a definition read abandoned halfway (another choice in the tree) has to finish before the
    /// <c>KILL QUERY</c>: it is short, it ends up in the cache, and cancelling it opened another connection. Only a stuck read is cancelled.
    /// </summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(3);

    public SessionHost Host { get; } = host;

    public MetadataOptions Options { get; } = options;

    public event EventHandler<MetadataChangedEventArgs>? Changed;

    /// <summary>Databases already cached, or <c>null</c>.</summary>
    public IReadOnlyList<string>? CachedDatabases
    {
        get
        {
            lock (_gate)
            {
                return _databases;
            }
        }
    }

    public DatabaseObjects? CachedObjects(string database)
    {
        lock (_gate)
        {
            return _objects.GetValueOrDefault(database);
        }
    }

    public TableDefinition? CachedTable(DbObjectRef table)
    {
        lock (_gate)
        {
            return _tables.GetValueOrDefault(table);
        }
    }

    /// <summary>The session's databases (whitelist and Hide database pattern applied), in the server's order.</summary>
    public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_databases is not null)
            {
                return Task.FromResult(_databases);
            }
            return _loadingDatabases ??= LoadDatabasesAsync(_generation, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<string>> LoadDatabasesAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            IEnumerable<string> names;
            if (!string.IsNullOrWhiteSpace(Options.Databases))
            {
                names = Options.Databases.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                names = await Host.RunMetadataAsync((h, ct) => h.Metadata.GetCatalogsAsync(ct), cancellationToken).ConfigureAwait(false);
            }
            if (!string.IsNullOrWhiteSpace(Options.HideDatabasePattern))
            {
                Regex? hide = null;
                try
                {
                    hide = new Regex(Options.HideDatabasePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                }
                catch (ArgumentException)
                {
                    // Invalid regex: the list stays unfiltered (HeidiSQL logs it and carries on).
                }
                if (hide is not null)
                {
                    names = names.Where(n => !hide.IsMatch(n));
                }
            }
            var list = names.ToList();
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _databases = list;
                }
            }
            Changed?.Invoke(this, new MetadataChangedEventArgs(MetadataScope.Databases));
            return list;
        }
        finally
        {
            lock (_gate)
            {
                _loadingDatabases = null;
            }
        }
    }

    /// <summary>
    /// Objects of a database. Without a cache, asks only for the names (fast) and, with "Get full table status", schedules the
    /// statistics in the background. <paramref name="forceStatistics"/> is Shift+F5 ("Full status refresh").
    /// </summary>
    public Task<DatabaseObjects> GetObjectsAsync(string database, CancellationToken cancellationToken, bool forceStatistics = false)
    {
        ArgumentNullException.ThrowIfNull(database);
        lock (_gate)
        {
            if (_objects.TryGetValue(database, out var cached))
            {
                if (forceStatistics && !cached.HasStatistics)
                {
                    StartStatistics(database, _generation, ObjectDetail.Statistics);
                }
                return Task.FromResult(cached);
            }
            if (!_loadingObjects.TryGetValue(database, out var loading))
            {
                loading = LoadObjectsAsync(database, _generation, forceStatistics, cancellationToken);
                _loadingObjects[database] = loading;
            }
            return loading;
        }
    }

    private async Task<DatabaseObjects> LoadObjectsAsync(string database, int generation, bool forceStatistics, CancellationToken cancellationToken)
    {
        try
        {
            var names = await Host.RunMetadataAsync((h, ct) => h.Metadata.GetObjectsAsync(database, database, ObjectDetail.Names, ct), cancellationToken).ConfigureAwait(false);
            var result = new DatabaseObjects(database, names, ObjectDetail.Names, DateTimeOffset.UtcNow);
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return result;
                }
                _objects[database] = result;
                StartStatistics(database, generation, Options.FullTableStatus || forceStatistics ? ObjectDetail.Statistics : ObjectDetail.Details);
            }
            Changed?.Invoke(this, new MetadataChangedEventArgs(MetadataScope.Objects, database));
            return result;
        }
        finally
        {
            lock (_gate)
            {
                _loadingObjects.Remove(database);
            }
        }
    }

    /// <summary>
    /// Second phase in the background: statistics (sizes, rows) or only the details (engine, dates, collation) when "Get
    /// full table status" is off. On a large database they take time (50 s for <c>SHOW TABLE STATUS</c> of 5,000 InnoDB tables
    /// on MariaDB 10.4, 7 s for the details): they run on a job connection, so they do not hold the metadata one, where the
    /// table definitions (Data tab, table editor) and the tree were waiting. In single-connection mode they stay in the
    /// low-priority queue of the metadata connection, as before.
    /// </summary>
    private void StartStatistics(string database, int generation, ObjectDetail detail)
    {
        if (_loadingStatistics.ContainsKey(database))
        {
            // Shift+F5 while the details are being read: the statistics follow.
            if (detail == ObjectDetail.Statistics && _loadingDetail.GetValueOrDefault(database) != ObjectDetail.Statistics)
            {
                _pendingStatistics.Add(database);
            }
            return;
        }
        _loadingDetail[database] = detail;
        _loadingStatistics[database] = Task.Run(async () =>
        {
            try
            {
                var full = Host.Settings.SeparateMetadataConnection
                    ? await Host.RunJobAsync((h, ct) => h.Metadata.GetObjectsAsync(database, database, detail, ct), CancellationToken.None).ConfigureAwait(false)
                    : await Host.RunMetadataAsync((h, ct) => h.Metadata.GetObjectsAsync(database, database, detail, ct), CancellationToken.None, WorkPriority.Statistics).ConfigureAwait(false);
                lock (_gate)
                {
                    if (generation != _generation)
                    {
                        return;
                    }
                    _objects[database] = new DatabaseObjects(database, full, detail, DateTimeOffset.UtcNow);
                }
                Changed?.Invoke(this, new MetadataChangedEventArgs(MetadataScope.Objects, database));
            }
#pragma warning disable CA1031 // Statistics are optional: an error (permissions, session closing) must not bring anything down.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            finally
            {
                lock (_gate)
                {
                    // After a refresh (new generation), the entries already come from another read.
                    if (generation == _generation)
                    {
                        _loadingStatistics.Remove(database);
                        _loadingDetail.Remove(database);
                        if (_pendingStatistics.Remove(database))
                        {
                            StartStatistics(database, generation, ObjectDetail.Statistics);
                        }
                    }
                }
            }
        });
    }

    /// <summary>Waits for the statistics requested for the database (tests and measurements).</summary>
    public Task WaitForStatisticsAsync(string database)
    {
        lock (_gate)
        {
            return _loadingStatistics.GetValueOrDefault(database) ?? Task.CompletedTask;
        }
    }

    /// <summary>Default collation of each database (Host tab), requested once until the next refresh.</summary>
    public Task<IReadOnlyDictionary<string, string>> GetDatabaseCollationsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_collations is { IsFaulted: false, IsCanceled: false } cached)
            {
                return cached;
            }
            return _collations = Host.RunMetadataAsync((h, ct) => h.Metadata.GetCatalogCollationsAsync(ct), cancellationToken);
        }
    }

    /// <summary>
    /// Every table of the database with columns, keys and FKs, at once (Schema Visualizer). It is not cached: the
    /// diagram reads it again on refresh (SV-16).
    /// </summary>
    public Task<IReadOnlyList<TableDefinition>> GetSchemaAsync(string database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return Host.RunMetadataAsync((h, ct) => h.Metadata.GetSchemaAsync(database, database, ct), cancellationToken);
    }

    /// <summary>Columns, indexes, foreign keys and checks of a table (cached until the database is refreshed).</summary>
    public async Task<TableDefinition> GetTableAsync(DbObjectRef table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (CachedTable(table) is { } cached)
        {
            return cached;
        }
        var (definition, fetched) = await Host.RunMetadataAsync(async (h, ct) =>
        {
            // Consecutive requests for the same table (navigation preload, Data tab, table editor) wait in the same queue: the
            // next one uses the definition the previous one just brought, instead of going back to the server.
            if (CachedTable(table) is { } loaded)
            {
                return (loaded, false);
            }
            int epoch;
            lock (_gate)
            {
                epoch = _tableEpoch;
            }
            var fresh = await h.Metadata.GetTableAsync(table, ct).ConfigureAwait(false);
            lock (_gate)
            {
                // A refresh or an ALTER during the request keeps it out of the cache (the caller still gets it).
                if (epoch == _tableEpoch)
                {
                    _tables[table] = fresh;
                }
            }
            return (fresh, true);
        }, cancellationToken, abandonGrace: CancelGrace).ConfigureAwait(false);
        if (fetched)
        {
            Changed?.Invoke(this, new MetadataChangedEventArgs(MetadataScope.Table, table.Schema, table.Name));
        }
        return definition;
    }

    /// <summary>
    /// Server lists that do not change during the session (collations, engines): requested once and shared until a
    /// full refresh. The request does not depend on whoever made it first; cancelling only stops waiting for it.
    /// </summary>
    public Task<T> GetServerListAsync<T>(string key, Func<IDbConnectionHandle, CancellationToken, Task<T>> load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(load);
        Task<T> task;
        lock (_gate)
        {
            if (_serverLists.TryGetValue(key, out var existing) && existing is Task<T> { IsFaulted: false, IsCanceled: false } same)
            {
                task = same;
            }
            else
            {
                task = Host.RunMetadataAsync(load, CancellationToken.None);
                _serverLists[key] = task;
            }
        }
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Refresh (F5): without a database, forgets everything (database list included); with a database, only that database and
    /// its tables (Database tab). Requests in flight from before the refresh are discarded when they arrive.
    /// </summary>
    public void Invalidate(string? database = null)
    {
        lock (_gate)
        {
            if (database is null)
            {
                _generation++;
                _tableEpoch++;
                _serverLists.Clear();
                _databases = null;
                _objects.Clear();
                _tables.Clear();
                _loadingObjects.Clear();
                _loadingStatistics.Clear();
                _loadingDetail.Clear();
                _pendingStatistics.Clear();
                _loadingDatabases = null;
                _collations = null;
            }
            else
            {
                _tableEpoch++;
                _objects.Remove(database);
                _loadingObjects.Remove(database);
                foreach (var key in _tables.Keys.Where(k => k.Schema == database).ToList())
                {
                    _tables.Remove(key);
                }
            }
        }
        Changed?.Invoke(this, new MetadataChangedEventArgs(database is null ? MetadataScope.All : MetadataScope.Objects, database));
    }

    /// <summary>Forgets only the definition of one table (after an ALTER).</summary>
    public void InvalidateTable(DbObjectRef table)
    {
        lock (_gate)
        {
            _tableEpoch++;
            _tables.Remove(table);
        }
        Changed?.Invoke(this, new MetadataChangedEventArgs(MetadataScope.Table, table.Schema, table.Name));
    }
}
