using System.Globalization;
using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Metadata;
using Eunomia.Engine.Sql.Splitting;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>
/// Copy table (anexo B §5.1) contra o servidor: estrutura e dados iguais aos da origem, filtro WHERE, colunas parciais e
/// nunca escrever numa tabela que já existe.
/// </summary>
public sealed class TableCopyTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private string _database = string.Empty;
    private string _other = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Rows = "(1, 'ação', 1, NULL), (2, 'O''Neil', NULL, 'C:\\\\dir\\\\a.txt'), (3, '漢字 😀', 2, 'tab\tline\nend')";

    public async ValueTask InitializeAsync()
    {
        if (TestServer.ConnectionString is null)
        {
            return;
        }
        _database = "eunomia_cp_" + Guid.NewGuid().ToString("N")[..8];
        _other = _database + "_b";
        await ExecAsync($"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4; CREATE DATABASE `{_other}` CHARACTER SET utf8mb4; " +
            $"CREATE TABLE `{_database}`.cities (id INT PRIMARY KEY) ENGINE=InnoDB; INSERT INTO `{_database}`.cities VALUES (1), (2); " +
            $"CREATE TABLE `{_database}`.people (id INT NOT NULL AUTO_INCREMENT, name VARCHAR(20) NOT NULL DEFAULT 'x', city_id INT NULL, note TEXT NULL, " +
            "shout VARCHAR(30) AS (CONCAT(name, '!')) VIRTUAL, PRIMARY KEY (id), UNIQUE KEY uq_name (name), KEY ix_city (city_id), " +
            $"CONSTRAINT fk_city FOREIGN KEY (city_id) REFERENCES `{_database}`.cities (id), CONSTRAINT positive CHECK (city_id IS NULL OR city_id > 0)) ENGINE=InnoDB; " +
            $"INSERT INTO `{_database}`.people (id, name, city_id, note) VALUES {Rows};");
        _host = await ConnectAsync(readOnly: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            // A cópia na outra base referencia a primeira: apaga-se antes.
            await ExecAsync($"DROP DATABASE IF EXISTS `{_other}`; DROP DATABASE IF EXISTS `{_database}`");
        }
    }

    private static async Task<SessionHost> ConnectAsync(bool readOnly)
    {
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString ?? string.Empty).Password;
        var host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null, ReadOnly = readOnly }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
            SplitScript = s => [.. SqlSplitter.Split(s).Select(x => x.Text)],
        });
        await host.ConnectAsync(Ct);
        return host;
    }

    private static async Task ExecAsync(string sql)
    {
        await using var connection = new MySqlConnection(TestServer.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var cmd = new MySqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<List<string>> RowsAsync(string sql)
    {
        await using var connection = new MySqlConnection(TestServer.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var cmd = new MySqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        }
        return rows;
    }

    private Task<TableDefinition> PeopleAsync() =>
        new MetadataCache(_host, new MetadataOptions()).GetTableAsync(new DbObjectRef(_database, _database, "people", DbObjectKind.Table), Ct);

    private static TableCopyRequest All(TableDefinition source, string database, string name, bool data = true, string? where = null) => new(
        database, name, [.. source.Columns.Select(c => c.Name)], [.. source.Indexes.Select(i => i.Name)], [.. source.ForeignKeys.Select(f => f.Name)], data, where);

    private async Task CopyAsync(TableDefinition source, TableCopyRequest request) =>
        await ObjectActions.RunAsync(_host, TableCopy.Statements(_host.ServerServices!, source, request), Ct);

    [Fact]
    public async Task CopiaEstruturaEDadosSemPerdas()
    {
        TestServer.RequireServer();
        var source = await PeopleAsync();

        await CopyAsync(source, All(source, _database, "people_copy"));

        const string Columns = "id, name, city_id, note, shout";
        Assert.Equal(await RowsAsync($"SELECT {Columns} FROM `{_database}`.people ORDER BY id"), await RowsAsync($"SELECT {Columns} FROM `{_database}`.people_copy ORDER BY id"));
        var create = (await RowsAsync($"SHOW CREATE TABLE `{_database}`.people_copy")).Single();
        Assert.Contains("AUTO_INCREMENT", create, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY `uq_name`", create, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT `people_copy_ibfk_1` FOREIGN KEY", create, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT `people_copy_chk_1` CHECK", create, StringComparison.Ordinal);
        Assert.Contains("VIRTUAL", create, StringComparison.Ordinal);
        // A origem continua igual.
        Assert.Equal(3, (await RowsAsync($"SELECT id FROM `{_database}`.people")).Count);
    }

    [Fact]
    public async Task FiltroCopiaSoAsLinhasPedidas()
    {
        TestServer.RequireServer();
        var source = await PeopleAsync();

        await CopyAsync(source, All(source, _database, "some", where: "WHERE id > 1;"));

        Assert.Equal(["2", "3"], await RowsAsync($"SELECT id FROM `{_database}`.some ORDER BY id"));
    }

    [Fact]
    public async Task FiltroComVariasInstrucoesEhRecusadoAntesDeCriar()
    {
        TestServer.RequireServer();
        var source = await PeopleAsync();

        Assert.Throws<InvalidOperationException>(() => TableCopy.Statements(_host.ServerServices!, source,
            All(source, _database, "evil", where: $"1=1; DROP TABLE `{_database}`.people")));

        Assert.Empty(await RowsAsync($"SHOW TABLES FROM `{_database}` LIKE 'evil'"));
        Assert.Equal(3, (await RowsAsync($"SELECT id FROM `{_database}`.people")).Count);
    }

    [Fact]
    public async Task ColunasSemAChavePerdemOAutoIncrement()
    {
        TestServer.RequireServer();
        var source = await PeopleAsync();

        await CopyAsync(source, new TableCopyRequest(_database, "names", ["id", "name"], [], [], Data: true, Where: null));

        var create = (await RowsAsync($"SHOW CREATE TABLE `{_database}`.names")).Single();
        Assert.DoesNotContain("AUTO_INCREMENT", create.Split('\n')[1], StringComparison.Ordinal);
        Assert.Equal(["1|ação", "2|O'Neil", "3|漢字 😀"], await RowsAsync($"SELECT id, name FROM `{_database}`.names ORDER BY id"));
    }

    [Fact]
    public async Task DestinoQueJaExisteNaoEhTocado()
    {
        TestServer.RequireServer();
        var source = await PeopleAsync();
        await ExecAsync($"CREATE TABLE `{_database}`.taken (id INT PRIMARY KEY, other TEXT); INSERT INTO `{_database}`.taken VALUES (100, 'keep');");

        await Assert.ThrowsAnyAsync<Exception>(() => CopyAsync(source, All(source, _database, "taken")));

        Assert.Equal(["100|keep"], await RowsAsync($"SELECT * FROM `{_database}`.taken"));
    }

    [Fact]
    public async Task OutraBaseMantemOsNomesDasRestricoes()
    {
        TestServer.RequireServer();
        var source = await PeopleAsync();

        await CopyAsync(source, All(source, _other, "people"));

        var create = (await RowsAsync($"SHOW CREATE TABLE `{_other}`.people")).Single();
        Assert.Contains("CONSTRAINT `fk_city` FOREIGN KEY", create, StringComparison.Ordinal);
        Assert.Contains($"REFERENCES `{_database}`.`cities`", create, StringComparison.Ordinal);
        Assert.Equal(3, (await RowsAsync($"SELECT id FROM `{_other}`.people")).Count);
    }

    [Fact]
    public async Task SessaoReadOnlyRecusaACopia()
    {
        TestServer.RequireServer();
        var source = await PeopleAsync();
        await using var readOnly = await ConnectAsync(readOnly: true);

        await Assert.ThrowsAnyAsync<Exception>(() => ObjectActions.RunAsync(readOnly, TableCopy.Statements(readOnly.ServerServices!, source, All(source, _database, "ro_copy")), Ct));

        Assert.Empty(await RowsAsync($"SHOW TABLES FROM `{_database}` LIKE 'ro_copy'"));
    }
}
