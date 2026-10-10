namespace Eunomia.Drivers.Abstractions;

/// <summary>Object kinds of the section 14.2 model.</summary>
public enum DbObjectKind
{
    Table,
    View,
    MaterializedView,
    Procedure,
    Function,
    Trigger,
    Event,
    Sequence,
    Type,
}

/// <summary>
/// Address of an object: <c>Server → Catalog → Schema → Object</c> (14.2). On MySQL/MariaDB catalog and schema
/// are the database; on PostgreSQL the catalog is the connection's database.
/// </summary>
public sealed record DbObjectRef(string Catalog, string Schema, string Name, DbObjectKind Kind);

/// <summary>An object as it shows in the tree (name, kind, size, estimated count, comment).</summary>
public sealed record DbObjectInfo(
    DbObjectRef Ref,
    long? DataBytes = null,
    long? IndexBytes = null,
    long? RowsEstimate = null,
    string? Engine = null,
    string? Comment = null,
    DateTime? Updated = null)
{
    /// <summary>Creation date (Created column of the Database tab).</summary>
    public DateTime? Created { get; init; }

    /// <summary>
    /// Every server status field (on MySQL, the <c>SHOW TABLE STATUS</c> columns), for the optional columns
    /// of the Database tab (Version, Row format, Avg row length…).
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Status { get; init; }

    /// <summary>Size in the tree: Data_length + Index_length (section 4.2).</summary>
    public long? SizeBytes => DataBytes is null && IndexBytes is null ? null : (DataBytes ?? 0) + (IndexBytes ?? 0);
}

/// <summary>
/// A column. <see cref="DefaultExpression"/> is the SQL that comes after <c>DEFAULT</c>, the same on MySQL and MariaDB:
/// quoted text, a number, <c>NULL</c> or an expression (<see cref="DefaultIsExpression"/>); <c>null</c> = no default.
/// </summary>
public sealed record ColumnDefinition(
    string Name,
    string NativeType,
    LogicalType Type,
    bool IsNullable,
    string? DefaultExpression,
    bool IsAutoIncrement,
    bool IsGenerated,
    bool IsInvisible,
    string? Comment)
{
    /// <summary>The default is an expression (<c>CURRENT_TIMESTAMP</c>, <c>(uuid())</c>) and not a literal.</summary>
    public bool DefaultIsExpression { get; init; }

    /// <summary><c>ON UPDATE</c> (for example <c>CURRENT_TIMESTAMP</c>).</summary>
    public string? OnUpdate { get; init; }

    /// <summary>Expression of a generated column.</summary>
    public string? GenerationExpression { get; init; }

    /// <summary>Stored generated column (STORED/PERSISTENT) instead of VIRTUAL.</summary>
    public bool IsStored { get; init; }

    /// <summary>Name on the server before editing (table editor: CHANGE COLUMN); <c>null</c> for a new column.</summary>
    public string? OriginalName { get; init; }
}

public enum IndexKind
{
    Primary,
    Unique,
    Key,
    FullText,
    Spatial,
}

public sealed record IndexDefinition(string Name, IndexKind Kind, IReadOnlyList<string> Columns, bool IsVisible = true)
{
    /// <summary>Prefix of each column (<c>col(20)</c>), in column order; <c>null</c> without prefixes.</summary>
    public IReadOnlyList<int?>? SubParts { get; init; }

    /// <summary>BTREE, HASH or RTREE when declared; <c>null</c> = the engine's.</summary>
    public string? Algorithm { get; init; }

    public string? Comment { get; init; }
}

public enum ForeignKeyAction
{
    Restrict,
    Cascade,
    SetNull,
    NoAction,
    SetDefault,
}

public sealed record ForeignKeyDefinition(
    string Name,
    IReadOnlyList<string> Columns,
    DbObjectRef ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    ForeignKeyAction OnUpdate,
    ForeignKeyAction OnDelete);

public sealed record CheckDefinition(string Name, string Expression);

/// <summary>Full structure of a table, the unit of the table editor and of the <see cref="IDdlComposer"/>.</summary>
public sealed record TableDefinition(
    DbObjectRef Ref,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<IndexDefinition> Indexes,
    IReadOnlyList<ForeignKeyDefinition> ForeignKeys,
    IReadOnlyList<CheckDefinition> Checks,
    IReadOnlyDictionary<string, string> Options);

public sealed record ServerVariable(string Name, string Value, bool IsSession);

/// <summary>An entry of a server HELP list: topic or category.</summary>
public sealed record SqlHelpEntry(string Name, bool IsCategory);

/// <summary>
/// Server HELP answer (SQL help, F1): a topic (<see cref="Name"/>, description and example) or, when the term
/// is a category or matches several topics, the list in <see cref="Entries"/>.
/// </summary>
public sealed record SqlHelp(string? Name, string? Description, string? Example, IReadOnlyList<SqlHelpEntry> Entries)
{
    public bool IsTopic => Name is not null;
}

public sealed record ServerProcess(long Id, string User, string Host, string? Database, string Command, TimeSpan Time, string? State, string? Info);

/// <summary>
/// How much to ask for when reading a schema's objects (PR-08). On servers with a file per table (MariaDB, MySQL 5.x), the
/// details force opening every table and cost as much as the statistics; names and kinds do not.
/// </summary>
public enum ObjectDetail
{
    /// <summary>Name and kind (table or view), routines, triggers and events: what the tree needs.</summary>
    Names,

    /// <summary>Plus engine, dates, collation and comment, without sizes or rows ("Get full table status" off).</summary>
    Details,

    /// <summary>Everything, with sizes and estimated rows (<c>SHOW TABLE STATUS</c>).</summary>
    Statistics,
}

public sealed record CollationInfo(string Name, string Charset, bool IsDefault);

/// <summary>
/// Server catalog (section 14.1). Batched queries per database (#1672); expensive statistics are requested separately
/// and are optional.
/// </summary>
public interface IMetadataProvider
{
    Task<IReadOnlyList<string>> GetCatalogsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetSchemasAsync(string catalog, CancellationToken cancellationToken);

    /// <summary>Every object of a schema in a single round trip, with the requested level of detail.</summary>
    Task<IReadOnlyList<DbObjectInfo>> GetObjectsAsync(string catalog, string schema, ObjectDetail detail, CancellationToken cancellationToken);

    Task<TableDefinition> GetTableAsync(DbObjectRef table, CancellationToken cancellationToken);

    Task<string> GetCreateCodeAsync(DbObjectRef obj, CancellationToken cancellationToken);

    /// <summary>
    /// Every table of a schema with columns, primary and unique keys and FKs, in few round trips (Schema
    /// Visualizer, section 17). No defaults, checks or options; FK actions are left as RESTRICT.
    /// </summary>
    Task<IReadOnlyList<TableDefinition>> GetSchemaAsync(string catalog, string schema, CancellationToken cancellationToken);

    /// <summary><c>SHOW CREATE DATABASE</c> (the database block in the SQL export).</summary>
    Task<string> GetCreateDatabaseAsync(string database, CancellationToken cancellationToken);

    /// <summary>SQL_MODE the trigger was created with (the SQL export restores it around the <c>CREATE TRIGGER</c>).</summary>
    Task<string> GetTriggerSqlModeAsync(DbObjectRef trigger, CancellationToken cancellationToken);

    Task<IReadOnlyList<ServerVariable>> GetVariablesAsync(bool session, CancellationToken cancellationToken);

    /// <summary><c>HELP 'term'</c>: the topic, or the list of matching topics and categories (SQL help, F1).</summary>
    Task<SqlHelp> GetHelpAsync(string topic, CancellationToken cancellationToken);

    /// <summary><c>SHOW GLOBAL STATUS</c> (Host tab: Status and Command-Statistics).</summary>
    Task<IReadOnlyList<ServerVariable>> GetStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ServerProcess>> GetProcessesAsync(CancellationToken cancellationToken);

    /// <summary>Default collation of each database ("Default collation" column of the Host tab).</summary>
    Task<IReadOnlyDictionary<string, string>> GetCatalogCollationsAsync(CancellationToken cancellationToken);

    /// <summary>Server collations (Create database dialog).</summary>
    Task<IReadOnlyList<CollationInfo>> GetCollationsAsync(CancellationToken cancellationToken);
}
