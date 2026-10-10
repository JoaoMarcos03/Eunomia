namespace Eunomia.Drivers.Abstractions;

/// <summary>Tipos de objeto do modelo da secção 14.2.</summary>
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
/// Endereço de um objeto: <c>Server → Catalog → Schema → Object</c> (14.2). Em MySQL/MariaDB catalog e schema
/// são a database; em PostgreSQL o catalog é a database da ligação.
/// </summary>
public sealed record DbObjectRef(string Catalog, string Schema, string Name, DbObjectKind Kind);

/// <summary>Um objeto como aparece na árvore (nome, tipo, tamanho, contagem estimada, comentário).</summary>
public sealed record DbObjectInfo(
    DbObjectRef Ref,
    long? DataBytes = null,
    long? IndexBytes = null,
    long? RowsEstimate = null,
    string? Engine = null,
    string? Comment = null,
    DateTime? Updated = null)
{
    /// <summary>Data de criação (coluna Created do Database tab).</summary>
    public DateTime? Created { get; init; }

    /// <summary>
    /// Todos os campos de estado do servidor (no MySQL, as colunas do <c>SHOW TABLE STATUS</c>), para as colunas opcionais
    /// do Database tab (Version, Row format, Avg row length…).
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Status { get; init; }

    /// <summary>Tamanho da árvore: Data_length + Index_length (secção 4.2).</summary>
    public long? SizeBytes => DataBytes is null && IndexBytes is null ? null : (DataBytes ?? 0) + (IndexBytes ?? 0);
}

/// <summary>
/// Uma coluna. <see cref="DefaultExpression"/> é o SQL que vem depois de <c>DEFAULT</c>, igual em MySQL e MariaDB:
/// texto entre aspas, número, <c>NULL</c> ou expressão (<see cref="DefaultIsExpression"/>); <c>null</c> = sem default.
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
    /// <summary>O default é uma expressão (<c>CURRENT_TIMESTAMP</c>, <c>(uuid())</c>) e não um literal.</summary>
    public bool DefaultIsExpression { get; init; }

    /// <summary><c>ON UPDATE</c> (por exemplo <c>CURRENT_TIMESTAMP</c>).</summary>
    public string? OnUpdate { get; init; }

    /// <summary>Expressão de uma coluna gerada.</summary>
    public string? GenerationExpression { get; init; }

    /// <summary>Coluna gerada guardada (STORED/PERSISTENT) em vez de VIRTUAL.</summary>
    public bool IsStored { get; init; }

    /// <summary>Nome no servidor antes de editar (table editor: CHANGE COLUMN); <c>null</c> numa coluna nova.</summary>
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
    /// <summary>Prefixo de cada coluna (<c>col(20)</c>), pela ordem das colunas; <c>null</c> sem prefixos.</summary>
    public IReadOnlyList<int?>? SubParts { get; init; }

    /// <summary>BTREE, HASH ou RTREE quando declarado; <c>null</c> = o do motor.</summary>
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

/// <summary>Estrutura completa de uma tabela, a unidade do table editor e do <see cref="IDdlComposer"/>.</summary>
public sealed record TableDefinition(
    DbObjectRef Ref,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<IndexDefinition> Indexes,
    IReadOnlyList<ForeignKeyDefinition> ForeignKeys,
    IReadOnlyList<CheckDefinition> Checks,
    IReadOnlyDictionary<string, string> Options);

public sealed record ServerVariable(string Name, string Value, bool IsSession);

/// <summary>Uma entrada de uma lista do HELP do servidor: tópico ou categoria.</summary>
public sealed record SqlHelpEntry(string Name, bool IsCategory);

/// <summary>
/// Resposta do HELP do servidor (SQL help, F1): um tópico (<see cref="Name"/>, descrição e exemplo) ou, quando o termo
/// é uma categoria ou corresponde a vários tópicos, a lista em <see cref="Entries"/>.
/// </summary>
public sealed record SqlHelp(string? Name, string? Description, string? Example, IReadOnlyList<SqlHelpEntry> Entries)
{
    public bool IsTopic => Name is not null;
}

public sealed record ServerProcess(long Id, string User, string Host, string? Database, string Command, TimeSpan Time, string? State, string? Info);

/// <summary>
/// Quanto pedir ao ler os objetos de um schema (PR-08). Em servidores com ficheiros por tabela (MariaDB, MySQL 5.x), os
/// detalhes obrigam a abrir cada tabela e custam o mesmo que as estatísticas; os nomes e o tipo não.
/// </summary>
public enum ObjectDetail
{
    /// <summary>Nome e tipo (tabela ou view), routines, triggers e events: o que a árvore precisa.</summary>
    Names,

    /// <summary>Mais engine, datas, collation e comentário, sem tamanhos nem linhas ("Get full table status" desligado).</summary>
    Details,

    /// <summary>Tudo, com tamanhos e linhas estimadas (<c>SHOW TABLE STATUS</c>).</summary>
    Statistics,
}

public sealed record CollationInfo(string Name, string Charset, bool IsDefault);

/// <summary>
/// Catálogo do servidor (secção 14.1). Queries em lote por base (#1672); estatísticas caras são pedidas à parte
/// e são opcionais.
/// </summary>
public interface IMetadataProvider
{
    Task<IReadOnlyList<string>> GetCatalogsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetSchemasAsync(string catalog, CancellationToken cancellationToken);

    /// <summary>Todos os objetos de um schema numa só ida ao servidor, com o nível de detalhe pedido.</summary>
    Task<IReadOnlyList<DbObjectInfo>> GetObjectsAsync(string catalog, string schema, ObjectDetail detail, CancellationToken cancellationToken);

    Task<TableDefinition> GetTableAsync(DbObjectRef table, CancellationToken cancellationToken);

    Task<string> GetCreateCodeAsync(DbObjectRef obj, CancellationToken cancellationToken);

    /// <summary>
    /// Todas as tabelas de um schema com colunas, chaves primárias e únicas e FKs, em poucas idas ao servidor (Schema
    /// Visualizer, secção 17). Sem defaults, checks nem opções; as ações das FKs ficam em RESTRICT.
    /// </summary>
    Task<IReadOnlyList<TableDefinition>> GetSchemaAsync(string catalog, string schema, CancellationToken cancellationToken);

    /// <summary><c>SHOW CREATE DATABASE</c> (bloco da base no SQL export).</summary>
    Task<string> GetCreateDatabaseAsync(string database, CancellationToken cancellationToken);

    /// <summary>SQL_MODE com que o trigger foi criado (o SQL export repõe-no à volta do <c>CREATE TRIGGER</c>).</summary>
    Task<string> GetTriggerSqlModeAsync(DbObjectRef trigger, CancellationToken cancellationToken);

    Task<IReadOnlyList<ServerVariable>> GetVariablesAsync(bool session, CancellationToken cancellationToken);

    /// <summary><c>HELP 'termo'</c>: o tópico, ou a lista de tópicos e categorias que correspondem (SQL help, F1).</summary>
    Task<SqlHelp> GetHelpAsync(string topic, CancellationToken cancellationToken);

    /// <summary><c>SHOW GLOBAL STATUS</c> (Host tab: Status e Command-Statistics).</summary>
    Task<IReadOnlyList<ServerVariable>> GetStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ServerProcess>> GetProcessesAsync(CancellationToken cancellationToken);

    /// <summary>Collation por omissão de cada base (coluna "Default collation" do Host tab).</summary>
    Task<IReadOnlyDictionary<string, string>> GetCatalogCollationsAsync(CancellationToken cancellationToken);

    /// <summary>Collations do servidor (diálogo Create database).</summary>
    Task<IReadOnlyList<CollationInfo>> GetCollationsAsync(CancellationToken cancellationToken);
}
