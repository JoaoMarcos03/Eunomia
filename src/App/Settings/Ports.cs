namespace Eunomia.App.Settings;

/// <summary>Porta das settings globais (secção 21.4). A implementação está em <c>Infra.Settings</c>.</summary>
public interface ISettingsStore
{
    /// <summary>Lê as settings; devolve os defaults se o ficheiro não existir ou estiver corrompido.</summary>
    AppSettings Load();

    /// <summary>Grava de forma atómica (ficheiro temporário + rename).</summary>
    void Save(AppSettings settings);
}

/// <summary>Tabela de uma sessão, chave do estado por tabela no <c>app.db</c>.</summary>
public readonly record struct TableKey(string Session, string Database, string Table);

/// <summary>Uma query executada (helper "Query history", anexo C §1.11).</summary>
public sealed record QueryHistoryEntry(long Id, string Session, string? Database, string Sql, DateTimeOffset ExecutedAt, TimeSpan Duration);

/// <summary>
/// Porta do <c>app.db</c> (secção 21.4): estado por tabela (colunas ocultas, filtro, sort), filtros recentes,
/// favoritos e histórico de queries, sem o limite de 1 MB do histórico do HeidiSQL. Thread-safe.
/// </summary>
public interface IAppDatabase : IDisposable
{
    string? GetTableState(TableKey table, string name);

    /// <summary>Grava um valor de estado; <c>null</c> apaga-o.</summary>
    void SetTableState(TableKey table, string name, string? value);

    /// <summary>Filtros recentes da tabela, do mais recente para o mais antigo.</summary>
    IReadOnlyList<string> GetRecentFilters(TableKey table);

    /// <summary>Acrescenta (ou sobe para o topo) e corta em <paramref name="max"/> entradas.</summary>
    void AddRecentFilter(TableKey table, string filter, int max = 20);

    void ClearRecentFilters(TableKey table);

    /// <summary>Favoritos da sessão no formato do HeidiSQL <c>db|schema|nome</c> (FAM-T03).</summary>
    IReadOnlyList<string> GetFavorites(string session);

    void SetFavorite(string session, string path, bool favorite);

    long AddHistory(string session, string? database, string sql, DateTimeOffset executedAt, TimeSpan duration);

    /// <summary>Histórico da sessão, do mais recente para o mais antigo.</summary>
    IReadOnlyList<QueryHistoryEntry> GetHistory(string session, int limit = 1000);

    void DeleteHistory(long id);

    void ClearHistory(string session);
}
