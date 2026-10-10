using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Execution.Editing;
using Eunomia.Engine.Metadata;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Gravação de linhas editadas no grid contra o servidor (Fase 7; anexo D B15; D3; FAM-G08, G09).</summary>
public sealed class RowWriterTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private MetadataCache _metadata = null!;
    private string _database = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (TestServer.ConnectionString is null)
        {
            return;
        }
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString).Password;
        _host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
        });
        await _host.ConnectAsync(Ct);
        _metadata = new MetadataCache(_host, new MetadataOptions());
        _database = "eunomia_rw_" + Guid.NewGuid().ToString("N")[..8];
        await _host.RunPrimaryAsync(async (h, ct) =>
        {
            await h.ExecuteNonQueryAsync($"CREATE DATABASE `{_database}`", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.typed (id INT AUTO_INCREMENT PRIMARY KEY, i INT, d DECIMAL(12,3), f DOUBLE, " +
                "b BIT(4), bin VARBINARY(8), dt DATETIME(3), t TEXT, e ENUM('a','b'), s SET('x','y'), j JSON)", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.loose (a INT, b VARCHAR(10))", ct);
            await h.ExecuteNonQueryAsync($"INSERT INTO `{_database}`.loose VALUES (1, 'x'), (1, 'x'), (2, NULL)", ct);
            return true;
        }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS `{_database}`", ct), CancellationToken.None);
            await _host.DisposeAsync();
        }
    }

    private Task<TableDefinition> TableAsync(string name) =>
        _metadata.GetTableAsync(new DbObjectRef(_database, _database, name, DbObjectKind.Table), Ct);

    private Task<object?> ScalarAsync(string sql) => _host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync(sql, ct), Ct);

    private static Dictionary<string, CellLiteral> Values(params (string Name, string? Text)[] values) =>
        values.ToDictionary(v => v.Name, v => new CellLiteral(v.Text), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task InsertUpdateDeleteComRoundTripPorTipo()
    {
        TestServer.RequireServer();
        var table = await TableAsync("typed");
        var writer = new RowWriter(_host);

        // INSERT só com as colunas alteradas; o auto incremento vem do LAST_INSERT_ID().
        var inserted = await writer.WriteAsync(table, new RowChange(RowChangeKind.Insert, Values(), Values(
            ("i", "-7"), ("d", "1234.5"), ("f", "0.25"), ("b", "0101"), ("bin", "0xCAFE"), ("dt", "2026-10-06 12:34:56.789"),
            ("t", "it's \\ \"ok\"\nnova linha"), ("e", "b"), ("s", "x,y"), ("j", "{\"a\": 1}"))), Ct);
        Assert.Equal(1, inserted.AffectedRows);
        var id = inserted.KeyAfter["id"].Text;
        Assert.Equal("1", id);
        var row = $"FROM `{_database}`.typed WHERE id = {id}";
        Assert.Equal("-7|1234.500|0.25|5|cafe|2026-10-06 12:34:56.789|it's \\ \"ok\"\nnova linha|b|x,y",
            await ScalarAsync($"SELECT CONCAT_WS('|', i, d, f, b + 0, LOWER(HEX(bin)), dt, t, e, s) {row}"));

        // UPDATE pela chave, com NULL e com uma função.
        var original = Values(("id", id));
        await writer.WriteAsync(table, new RowChange(RowChangeKind.Update, original, new Dictionary<string, CellLiteral>
        {
            ["t"] = CellLiteral.Null,
            ["dt"] = new("'2000-01-01' + INTERVAL 1 DAY", IsFunction: true),
        }), Ct);
        Assert.Equal("NULL|2000-01-02 00:00:00.000", await ScalarAsync($"SELECT CONCAT_WS('|', IFNULL(t, 'NULL'), dt) {row}"));

        // UPDATE sem mudar valores: o servidor conta 0 linhas, mas a linha existe.
        var same = await writer.WriteAsync(table, new RowChange(RowChangeKind.Update, original, Values(("i", "-7"))), Ct);
        Assert.Equal(1, same.AffectedRows);

        // DELETE; repetir dá o erro das 0 linhas.
        Assert.Equal(1, (await writer.WriteAsync(table, new RowChange(RowChangeKind.Delete, original, Values()), Ct)).AffectedRows);
        var error = await Assert.ThrowsAsync<RowWriteException>(() => writer.WriteAsync(table, new RowChange(RowChangeKind.Delete, original, Values()), Ct));
        Assert.StartsWith("The DELETE affected 0 rows, but 1 was expected.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TabelaSemChaveUsaTodasAsColunasELimit1()
    {
        TestServer.RequireServer();
        var table = await TableAsync("loose");
        Assert.False(RowKey.Of(table).IsUnique);
        var writer = new RowWriter(_host);

        // Duas linhas iguais: o UPDATE muda só uma (LIMIT 1).
        await writer.WriteAsync(table, new RowChange(RowChangeKind.Update, Values(("a", "1"), ("b", "x")), Values(("b", "y"))), Ct);
        Assert.Equal("x,y", await ScalarAsync($"SELECT GROUP_CONCAT(b ORDER BY b) FROM `{_database}`.loose WHERE a = 1"));

        // NULL no WHERE com IS NULL.
        await writer.WriteAsync(table, new RowChange(RowChangeKind.Delete, Values(("a", "2"), ("b", null)), Values()), Ct);
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.loose"), System.Globalization.CultureInfo.InvariantCulture));
    }
}
