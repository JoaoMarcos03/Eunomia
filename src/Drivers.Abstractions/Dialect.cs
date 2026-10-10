namespace Eunomia.Drivers.Abstractions;

/// <summary>Capabilities da secção 14.3 (flags; as que têm valores vivem em <see cref="ICapabilities"/>).</summary>
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

    /// <summary><c>CREATE OR REPLACE</c> de procedures, functions, triggers e events (as views têm-no sempre).</summary>
    CreateOrReplaceObjects,
}

/// <summary>
/// Matriz declarativa por servidor (secção 14.3). A UI consulta-a para ativar ou desativar comandos.
/// Os valores por versão validam-se contra a documentação de cada motor ao implementar o driver.
/// </summary>
public interface ICapabilities
{
    bool Supports(Capability capability);

    /// <summary>Carácter de citação de identificadores: backtick em MySQL/MariaDB, aspas duplas em PostgreSQL.</summary>
    char IdentifierQuote { get; }

    /// <summary>Formatos de EXPLAIN disponíveis, por ordem de preferência.</summary>
    IReadOnlyList<string> ExplainFormats { get; }
}

/// <summary>Operador de comparação textual dos filtros gerados (FAM-G12, G13).</summary>
public enum LikeOperator
{
    Like,
    ILike,
}

/// <summary>
/// SQL específico do motor (secção 14.1), tipado: cada operação recebe argumentos estruturados e devolve SQL
/// pronto, sem templates de string soltos noutras camadas.
/// </summary>
public interface IDialect
{
    string QuoteIdentifier(string name);

    string QualifiedName(DbObjectRef obj);

    /// <summary>
    /// Literal de texto para uma expressão (valores, WHERE, DEFAULT). Lido da mesma forma pelo servidor em qualquer modo
    /// da sessão (em MySQL, com e sem <c>NO_BACKSLASH_ESCAPES</c>): um modo mudado sem o cliente saber nunca altera o valor gravado.
    /// </summary>
    string StringLiteral(string value);

    /// <summary>
    /// Texto entre aspas para os sítios da gramática que só aceitam uma string simples (<c>COMMENT</c>, <c>LIKE</c> do
    /// <c>SHOW</c>, <c>HELP</c>), com o escaping do modo da ligação de onde o dialeto veio.
    /// </summary>
    string PlainStringLiteral(string value);

    /// <summary>Cláusula de paginação: <c>LIMIT off, n</c> em MySQL, <c>LIMIT n OFFSET off</c> em PostgreSQL.</summary>
    string Paging(long offset, int count);

    /// <summary>
    /// Predicado de keyset (ADR-07): <c>(c1, c2) &gt; (v1, v2)</c> com os valores já em literal; a direção vem de
    /// <paramref name="descending"/>.
    /// </summary>
    string KeysetPredicate(IReadOnlyList<string> columns, IReadOnlyList<string> literalValues, bool descending);

    string KillQuery(long serverConnectionId);

    /// <summary>Termina a ligação (Kill Process(es) do Host tab).</summary>
    string KillConnection(long serverConnectionId);

    string ChangeDatabase(string database);

    string Rename(DbObjectRef obj, string newName);

    string Truncate(DbObjectRef table);

    string Explain(string statement, string format);

    /// <summary>Contagem aproximada barata (como o <c>SHOW TABLE STATUS LIKE</c> do HeidiSQL).</summary>
    string EstimatedCount(DbObjectRef table);

    /// <summary>
    /// Instrução de manutenção de uma tabela ou view (Maintenance, anexo B §2.2) com as opções pedidas que a operação
    /// aceita; <c>null</c> se o motor não tiver a operação. Em MySQL: <c>CHECK/ANALYZE/CHECKSUM/OPTIMIZE/REPAIR TABLE</c>.
    /// </summary>
    string? Maintenance(MaintenanceOperation operation, DbObjectRef table, MaintenanceOptions options);

    /// <summary>Opções que a operação de manutenção aceita (as caixas ativas no diálogo); <see cref="MaintenanceOptions.None"/> se nenhuma.</summary>
    MaintenanceOptions MaintenanceOptionsOf(MaintenanceOperation operation);

    LikeOperator CaseInsensitiveLike { get; }

    /// <summary>
    /// Expressão que corta uma coluna longa a <paramref name="maxLength"/> caracteres no Data tab (anexo D B2), ou <c>null</c>
    /// se o tipo não se corta (números, datas, texto curto). Em MySQL: <c>LEFT(col, 256)</c>, com <c>CAST(col AS CHAR)</c> no JSON.
    /// </summary>
    string? LoadPartExpression(ColumnDefinition column, int maxLength);

    /// <summary>
    /// Coluna como texto para comparações (<c>CastAsText</c> do HeidiSQL): em MySQL, <c>CAST(col AS CHAR)</c> só nas datas, horas e
    /// JSON; as outras ficam como estão.
    /// </summary>
    string CastAsText(ColumnDefinition column);

    /// <summary>Desliga as verificações de chaves estrangeiras guardando o valor anterior; <c>null</c> se o motor não tiver.</summary>
    string? DisableForeignKeyChecks { get; }

    /// <summary>Repõe o valor guardado por <see cref="DisableForeignKeyChecks"/>.</summary>
    string? RestoreForeignKeyChecks { get; }

    /// <summary>SQL da ligação de job do SQL export (anexo B §2.3.2; secção 13.2).</summary>
    DumpSessionSql DumpSession { get; }

    /// <summary>
    /// Põe a sessão em transações só de leitura (sessões read-only: o servidor recusa escritas); <c>null</c> se o
    /// servidor não tiver.
    /// </summary>
    string? ReadOnlySession { get; }

    /// <summary>Lê o modo da sessão (<c>@@SESSION.sql_mode</c> em MySQL); <c>null</c> se o motor não tiver modos.</summary>
    string? SessionModeQuery { get; }

    /// <summary>
    /// Instruções à volta do DDL da aplicação e as que repõem a sessão: espera por locks de metadata no máximo
    /// <paramref name="lockWaitSeconds"/> (um ALTER à espera de um lock bloqueia todas as queries seguintes à tabela) e,
    /// com <paramref name="strictFrom"/> (o modo atual), um modo em que um ALTER que copia dados falha em vez de os
    /// cortar em silêncio. <c>null</c> se o motor não precisar.
    /// </summary>
    (string Enter, string Restore)? DdlSession(string? strictFrom, int lockWaitSeconds);
}

/// <summary>
/// Instruções do SQL export na ligação de job: <see cref="Setup"/> antes (no MySQL, <c>SQL_MODE=''</c> para o
/// <c>SHOW CREATE</c> não usar aspas ANSI e <c>TIME_ZONE='+00:00'</c> para os TIMESTAMP saírem em UTC),
/// <see cref="Restore"/> depois; <see cref="BeginSnapshot"/> e <see cref="EndSnapshot"/> para o snapshot consistente
/// (como o <c>mysqldump --single-transaction</c>); <see cref="ServerOsQuery"/> para o cabeçalho.
/// </summary>
public sealed record DumpSessionSql(
    IReadOnlyList<string> Setup,
    IReadOnlyList<string> Restore,
    IReadOnlyList<string> BeginSnapshot,
    string? EndSnapshot,
    string? ServerOsQuery);

/// <summary>CREATE/ALTER/DROP a partir de diff de modelo (secção 14.1); uma implementação por família.</summary>
public interface IDdlComposer
{
    IReadOnlyList<string> Create(TableDefinition table);

    /// <summary>Instruções que levam <paramref name="before"/> a <paramref name="after"/>, por ordem.</summary>
    IReadOnlyList<string> Alter(TableDefinition before, TableDefinition after);

    string Drop(DbObjectRef obj);

    /// <summary><c>CREATE DATABASE</c> com a collation opcional (anexo B §5.3).</summary>
    string CreateDatabase(string name, string? collation);

    string DropDatabase(string name);

    /// <summary>Definição de uma chave estrangeira (<c>CONSTRAINT … FOREIGN KEY … REFERENCES …</c>), para um ALTER ADD.</summary>
    string ForeignKeySpec(ForeignKeyDefinition fk);
}
