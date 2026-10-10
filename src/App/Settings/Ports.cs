namespace Eunomia.App.Settings;

/// <summary>Port for the global settings (section 21.4). The implementation is in <c>Infra.Settings</c>.</summary>
public interface ISettingsStore
{
    /// <summary>Reads the settings; returns the defaults if the file does not exist or is corrupted.</summary>
    AppSettings Load();

    /// <summary>Writes atomically (temporary file + rename).</summary>
    void Save(AppSettings settings);
}

/// <summary>A table of a session, the key of the per-table state in <c>app.db</c>.</summary>
public readonly record struct TableKey(string Session, string Database, string Table);

/// <summary>A query that ran ("Query history" helper, appendix C §1.11).</summary>
public sealed record QueryHistoryEntry(long Id, string Session, string? Database, string Sql, DateTimeOffset ExecutedAt, TimeSpan Duration);

/// <summary>
/// <c>app.db</c> port (section 21.4): per-table state (hidden columns, filter, sort), recent filters,
/// favourites and query history, without the 1 MB limit of HeidiSQL's history. Thread-safe.
/// </summary>
public interface IAppDatabase : IDisposable
{
    string? GetTableState(TableKey table, string name);

    /// <summary>Saves a state value; <c>null</c> deletes it.</summary>
    void SetTableState(TableKey table, string name, string? value);

    /// <summary>Recent filters of the table, from the most recent to the oldest.</summary>
    IReadOnlyList<string> GetRecentFilters(TableKey table);

    /// <summary>Adds (or moves to the top) and trims to <paramref name="max"/> entries.</summary>
    void AddRecentFilter(TableKey table, string filter, int max = 20);

    void ClearRecentFilters(TableKey table);

    /// <summary>The session's favourites in HeidiSQL's <c>db|schema|name</c> format (FAM-T03).</summary>
    IReadOnlyList<string> GetFavorites(string session);

    void SetFavorite(string session, string path, bool favorite);

    long AddHistory(string session, string? database, string sql, DateTimeOffset executedAt, TimeSpan duration);

    /// <summary>The session's history, from the most recent to the oldest.</summary>
    IReadOnlyList<QueryHistoryEntry> GetHistory(string session, int limit = 1000);

    void DeleteHistory(long id);

    void ClearHistory(string session);
}
