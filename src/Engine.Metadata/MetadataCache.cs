using System.Text.RegularExpressions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;

namespace Eunomia.Engine.Metadata;

/// <summary>Opções da sessão que mudam a lista de bases e o custo das queries (anexo A §5, §6).</summary>
public sealed record MetadataOptions
{
    /// <summary>"Get full table status": tamanhos e linhas em background depois dos nomes.</summary>
    public bool FullTableStatus { get; init; } = true;

    /// <summary>Campo Databases: lista literal separada por <c>;</c> que substitui o <c>SHOW DATABASES</c>.</summary>
    public string Databases { get; init; } = string.Empty;

    /// <summary>"Hide database pattern": regex que retira bases da lista.</summary>
    public string HideDatabasePattern { get; init; } = string.Empty;
}

/// <summary>Objetos de uma base em cache e o nível de detalhe já lido (<see cref="HasStatistics"/>: há tamanhos e linhas).</summary>
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

/// <summary>Metadata mudou (carregado, estatísticas chegaram, invalidado). Disparado em qualquer thread.</summary>
public sealed class MetadataChangedEventArgs(MetadataScope scope, string? database = null, string? objectName = null) : EventArgs
{
    public MetadataScope Scope { get; } = scope;

    public string? Database { get; } = database;

    public string? ObjectName { get; } = objectName;
}

/// <summary>
/// Cache de metadata de uma sessão (secção 21.1 <c>Engine.Metadata</c>; 12.3; PR-08). As bases e os objetos são pedidos na
/// ligação de metadata e ficam em cache até um refresh. Os objetos chegam em duas fases: primeiro só os nomes (query leve
/// ao <c>information_schema</c>, para a árvore aparecer logo) e, com "Get full table status", as estatísticas em
/// background (prioridade baixa), que atualizam a cache e disparam <see cref="Changed"/>. Pedidos iguais em simultâneo
/// partilham a mesma ida ao servidor. Thread-safe.
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

    /// <summary>Sobe em cada invalidação: uma definição pedida antes não entra na cache.</summary>
    private int _tableEpoch;

    /// <summary>
    /// Tempo que uma leitura de definição abandonada a meio (outra escolha na árvore) tem para acabar antes do
    /// <c>KILL QUERY</c>: é curta, fica na cache, e cancelá-la abria outra ligação. Só uma leitura presa é cancelada.
    /// </summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(3);

    public SessionHost Host { get; } = host;

    public MetadataOptions Options { get; } = options;

    public event EventHandler<MetadataChangedEventArgs>? Changed;

    /// <summary>Bases já em cache, ou <c>null</c>.</summary>
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

    /// <summary>Bases da sessão (whitelist e Hide database pattern aplicados), pela ordem do servidor.</summary>
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
                    // Regex inválida: a lista fica sem filtro (o HeidiSQL regista e continua).
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
    /// Objetos de uma base. Sem cache, pede só os nomes (rápido) e, com "Get full table status", agenda as estatísticas em
    /// background. <paramref name="forceStatistics"/> é o Shift+F5 ("Full status refresh").
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
    /// Segunda fase em background: estatísticas (tamanhos, linhas) ou só os detalhes (engine, datas, collation) quando "Get
    /// full table status" está desligado. Numa base grande demoram (50 s o <c>SHOW TABLE STATUS</c> de 5.000 tabelas InnoDB
    /// no MariaDB 10.4, 7 s os detalhes): correm numa ligação de job, para não prenderem a de metadata, onde esperavam as
    /// definições das tabelas (Data tab, table editor) e a árvore. No modo de ligação única ficam na fila de baixa
    /// prioridade da ligação de metadata, como antes.
    /// </summary>
    private void StartStatistics(string database, int generation, ObjectDetail detail)
    {
        if (_loadingStatistics.ContainsKey(database))
        {
            // Shift+F5 durante a leitura dos detalhes: as estatísticas seguem-se.
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
#pragma warning disable CA1031 // Estatísticas são opcionais: um erro (permissões, sessão a fechar) não pode derrubar nada.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            finally
            {
                lock (_gate)
                {
                    // Depois de um refresh (geração nova), as entradas já são de outra leitura.
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

    /// <summary>Espera pelas estatísticas pedidas para a base (testes e medições).</summary>
    public Task WaitForStatisticsAsync(string database)
    {
        lock (_gate)
        {
            return _loadingStatistics.GetValueOrDefault(database) ?? Task.CompletedTask;
        }
    }

    /// <summary>Collation por omissão de cada base (Host tab), pedida uma vez até ao próximo refresh.</summary>
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
    /// Todas as tabelas da base com colunas, chaves e FKs, de uma vez (Schema Visualizer). Não fica em cache: o
    /// diagrama relê-o no refresh (SV-16).
    /// </summary>
    public Task<IReadOnlyList<TableDefinition>> GetSchemaAsync(string database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return Host.RunMetadataAsync((h, ct) => h.Metadata.GetSchemaAsync(database, database, ct), cancellationToken);
    }

    /// <summary>Colunas, índices, chaves estrangeiras e checks de uma tabela (em cache até refresh da base).</summary>
    public async Task<TableDefinition> GetTableAsync(DbObjectRef table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (CachedTable(table) is { } cached)
        {
            return cached;
        }
        var (definition, fetched) = await Host.RunMetadataAsync(async (h, ct) =>
        {
            // Pedidos seguidos da mesma tabela (pré-carga da navegação, Data tab, table editor) esperam na mesma fila: o
            // seguinte usa a definição que o anterior acabou de trazer, em vez de voltar ao servidor.
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
                // Um refresh ou um ALTER durante o pedido deixam-no fora da cache (quem pediu recebe-o na mesma).
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
    /// Listas do servidor que não mudam durante a sessão (collations, engines): pedidas uma vez e partilhadas até um
    /// refresh completo. O pedido não depende de quem o fez primeiro; cancelar só deixa de esperar por ele.
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
    /// Refresh (F5): sem base, esquece tudo (lista de bases incluída); com base, só essa base e as suas tabelas
    /// (Database tab). Pedidos em curso de antes do refresh são descartados ao chegar.
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

    /// <summary>Esquece só a definição de uma tabela (depois de um ALTER).</summary>
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
