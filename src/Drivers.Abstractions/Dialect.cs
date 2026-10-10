namespace Eunomia.Drivers.Abstractions;

/// <summary>Capabilities of section 14.3 (flags; the ones with values live in <see cref="ICapabilities"/>).</summary>
public enum Capability
{
    RowValueComparison,
    KillQuery,
    TransactionalDdl,
    InvisibleColumns,
    CheckConstraintsEnforced,
    Returning,
    Sequences,
    SystemVersionedTables,
    Events,
    IndexVisibility,
    ProfileQuery,

    /// <summary><c>CREATE OR REPLACE</c> for procedures, functions, triggers and events (views always have it).</summary>
    CreateOrReplaceObjects,
}

/// <summary>
/// Declarative matrix per server (section 14.3). The UI checks it to enable or disable commands.
/// Per-version values are validated against each engine's documentation when the driver is implemented.
/// </summary>
public interface ICapabilities
{
    bool Supports(Capability capability);

    /// <summary>Identifier quote character: backtick on MySQL/MariaDB, double quotes on PostgreSQL.</summary>
    char IdentifierQuote { get; }

    /// <summary>Available EXPLAIN formats, in order of preference.</summary>
    IReadOnlyList<string> ExplainFormats { get; }
}

/// <summary>Text comparison operator of the generated filters (FAM-G12, G13).</summary>
public enum LikeOperator
{
    Like,
    ILike,
}

/// <summary>
/// Engine-specific SQL (section 14.1), typed: each operation takes structured arguments and returns ready SQL,
/// with no loose string templates in other layers.
/// </summary>
public interface IDialect
{
    string QuoteIdentifier(string name);

    string QualifiedName(DbObjectRef obj);

    /// <summary>
    /// Text literal for an expression (values, WHERE, DEFAULT). Read the same way by the server in any session mode
    /// (on MySQL, with and without <c>NO_BACKSLASH_ESCAPES</c>): a mode changed without the client knowing never alters the saved value.
    /// </summary>
    string StringLiteral(string value);

    /// <summary>
    /// Quoted text for the places in the grammar that only accept a simple string (<c>COMMENT</c>, <c>SHOW</c>'s
    /// <c>LIKE</c>, <c>HELP</c>), with the escaping of the mode of the connection the dialect came from.
    /// </summary>
    string PlainStringLiteral(string value);

    /// <summary>Paging clause: <c>LIMIT off, n</c> on MySQL, <c>LIMIT n OFFSET off</c> on PostgreSQL.</summary>
    string Paging(long offset, int count);

    /// <summary>
    /// Keyset predicate (ADR-07): <c>(c1, c2) &gt; (v1, v2)</c> with the values already as literals; the direction comes from
    /// <paramref name="descending"/>.
    /// </summary>
    string KeysetPredicate(IReadOnlyList<string> columns, IReadOnlyList<string> literalValues, bool descending);

    string KillQuery(long serverConnectionId);

    /// <summary>Ends the connection (Kill Process(es) in the Host tab).</summary>
    string KillConnection(long serverConnectionId);

    string ChangeDatabase(string database);

    string Rename(DbObjectRef obj, string newName);

    string Truncate(DbObjectRef table);

    string Explain(string statement, string format);

    /// <summary>Cheap approximate count (like HeidiSQL's <c>SHOW TABLE STATUS LIKE</c>).</summary>
    string EstimatedCount(DbObjectRef table);

    /// <summary>
    /// Maintenance statement for a table or view (Maintenance, appendix B §2.2) with the requested options the operation
    /// accepts; <c>null</c> if the engine does not have the operation. On MySQL: <c>CHECK/ANALYZE/CHECKSUM/OPTIMIZE/REPAIR TABLE</c>.
    /// </summary>
    string? Maintenance(MaintenanceOperation operation, DbObjectRef table, MaintenanceOptions options);

    /// <summary>Options the maintenance operation accepts (the enabled boxes in the dialog); <see cref="MaintenanceOptions.None"/> if none.</summary>
    MaintenanceOptions MaintenanceOptionsOf(MaintenanceOperation operation);

    LikeOperator CaseInsensitiveLike { get; }

    /// <summary>
    /// Expression that cuts a long column to <paramref name="maxLength"/> characters in the Data tab (appendix D B2), or <c>null</c>
    /// if the type is not cut (numbers, dates, short text). On MySQL: <c>LEFT(col, 256)</c>, with <c>CAST(col AS CHAR)</c> for JSON.
    /// </summary>
    string? LoadPartExpression(ColumnDefinition column, int maxLength);

    /// <summary>
    /// Column as text for comparisons (HeidiSQL's <c>CastAsText</c>): on MySQL, <c>CAST(col AS CHAR)</c> only for dates, times and
    /// JSON; the others stay as they are.
    /// </summary>
    string CastAsText(ColumnDefinition column);

    /// <summary>Turns foreign key checks off, saving the previous value; <c>null</c> if the engine does not have them.</summary>
    string? DisableForeignKeyChecks { get; }

    /// <summary>Restores the value saved by <see cref="DisableForeignKeyChecks"/>.</summary>
    string? RestoreForeignKeyChecks { get; }

    /// <summary>SQL of the SQL export job connection (appendix B §2.3.2; section 13.2).</summary>
    DumpSessionSql DumpSession { get; }

    /// <summary>
    /// Puts the session in read-only transactions (read-only sessions: the server refuses writes); <c>null</c> if the
    /// server does not have them.
    /// </summary>
    string? ReadOnlySession { get; }

    /// <summary>Reads the session mode (<c>@@SESSION.sql_mode</c> on MySQL); <c>null</c> if the engine has no modes.</summary>
    string? SessionModeQuery { get; }

    /// <summary>
    /// Statements around the application's DDL and those that restore the session: waits for metadata locks at most
    /// <paramref name="lockWaitSeconds"/> (an ALTER waiting for a lock blocks every later query on the table) and,
    /// with <paramref name="strictFrom"/> (the current mode), a mode in which an ALTER that copies data fails instead of
    /// truncating it silently. <c>null</c> if the engine does not need them.
    /// </summary>
    (string Enter, string Restore)? DdlSession(string? strictFrom, int lockWaitSeconds);
}

/// <summary>
/// SQL export statements on the job connection: <see cref="Setup"/> before (on MySQL, <c>SQL_MODE=''</c> so that
/// <c>SHOW CREATE</c> does not use ANSI quotes and <c>TIME_ZONE='+00:00'</c> so TIMESTAMPs come out in UTC),
/// <see cref="Restore"/> after; <see cref="BeginSnapshot"/> and <see cref="EndSnapshot"/> for the consistent snapshot
/// (like <c>mysqldump --single-transaction</c>); <see cref="ServerOsQuery"/> for the header.
/// </summary>
public sealed record DumpSessionSql(
    IReadOnlyList<string> Setup,
    IReadOnlyList<string> Restore,
    IReadOnlyList<string> BeginSnapshot,
    string? EndSnapshot,
    string? ServerOsQuery);

/// <summary>CREATE/ALTER/DROP from a model diff (section 14.1); one implementation per family.</summary>
public interface IDdlComposer
{
    IReadOnlyList<string> Create(TableDefinition table);

    /// <summary>Statements that take <paramref name="before"/> to <paramref name="after"/>, in order.</summary>
    IReadOnlyList<string> Alter(TableDefinition before, TableDefinition after);

    string Drop(DbObjectRef obj);

    /// <summary><c>CREATE DATABASE</c> with an optional collation (appendix B §5.3).</summary>
    string CreateDatabase(string name, string? collation);

    string DropDatabase(string name);

    /// <summary>Definition of a foreign key (<c>CONSTRAINT … FOREIGN KEY … REFERENCES …</c>), for an ALTER ADD.</summary>
    string ForeignKeySpec(ForeignKeyDefinition fk);
}
