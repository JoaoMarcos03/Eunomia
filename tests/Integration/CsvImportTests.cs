using System.Globalization;
using System.Text;
using Eunomia.App.Transfer;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Transfer;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>CSV import in server and client modes (phase 8; FAM-E07; PR-15; appendix B §3).</summary>
public sealed class CsvImportTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private string _database = string.Empty;
    private string _directory = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CsvTargetColumn[] Columns = [new("id", true), new("name", false), new("price", true), new("note", false)];

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
        _database = "eunomia_csv_" + Guid.NewGuid().ToString("N")[..8];
        _directory = Path.Combine(Path.GetTempPath(), "eunomia-it", "csv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        // MySQL 8 has local_infile off by default; the test server is for tests only.
        await ExecAsync("SET GLOBAL local_infile = 1");
        await ExecAsync($"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4");
        await ExecAsync($"CREATE TABLE `{_database}`.t (id INT PRIMARY KEY, name VARCHAR(50), price DECIMAL(10,2), note TEXT)");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await ExecAsync($"DROP DATABASE IF EXISTS `{_database}`");
            await _host.DisposeAsync();
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private Task ExecAsync(string sql) => _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync(sql, ct), CancellationToken.None);

    private async Task<string?> ScalarAsync(string sql) =>
        Convert.ToString(await _host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync(sql, ct), Ct), CultureInfo.InvariantCulture);

    private string Write(string name, string text, Encoding encoding)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text, encoding);
        return path;
    }

    private CsvImportRequest Request(string path, CsvImportMethod method) => new()
    {
        Path = path,
        Database = _database,
        Table = "t",
        Columns = Columns,
        Method = method,
    };

    private Task<string?> RowsAsync() =>
        ScalarAsync($"SELECT GROUP_CONCAT(CONCAT_WS('|', id, IFNULL(name, '<NULL>'), IFNULL(price, '<NULL>'), IFNULL(note, '<NULL>')) ORDER BY id SEPARATOR ' ; ') FROM `{_database}`.t");

    [Theory]
    [InlineData(CsvImportMethod.Server)]
    [InlineData(CsvImportMethod.Client)]
    public async Task DefaultsDoHeidiSqlComAspasDuplicadasEReplace(CsvImportMethod method)
    {
        TestServer.RequireServer();
        await ExecAsync($"INSERT INTO `{_database}`.t VALUES (2, 'antigo', 1, 'x')");
        const string csv = "id;name;price;note\r\n1;\"Ana; \"\"a\"\"\";10.5;NULL\r\n2;Rui;20;\"linha\r\nnova\"\r\n3;\"ação\";NULL;\r\n";
        var path = Write("a.csv", csv, new UTF8Encoding(false));
        var progress = new List<CsvImportProgress>();
        var result = await new CsvImportJob(_host, Request(path, method)).RunAsync(new SyncProgress<CsvImportProgress>(progress.Add), Ct);

        Assert.False(result.Cancelled);
        // REPLACE counts 2 for the replaced row.
        Assert.Equal(4, result.Rows);
        Assert.Equal(new FileInfo(path).Length, result.BytesRead);
        Assert.Equal("1|Ana; \"a\"|10.50|<NULL> ; 2|Rui|20.00|linha\r\nnova ; 3|ação|<NULL>|", await RowsAsync());
        Assert.NotEmpty(progress);
    }

    [Theory]
    [InlineData(CsvImportMethod.Server)]
    [InlineData(CsvImportMethod.Client)]
    public async Task Utf16ComNumerosLocaisETruncate(CsvImportMethod method)
    {
        TestServer.RequireServer();
        await ExecAsync($"INSERT INTO `{_database}`.t VALUES (99, 'apagar', 1, 'x')");
        var path = Write("b.csv", "id\tname\tprice\tnote\n1\tZé\t1.234,56\tum\n2\tÿ€\t-0,5\tdois\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        var request = Request(path, method) with
        {
            Csv = new CsvOptions { FieldTerminator = "\t", LineTerminator = "\n", Encloser = string.Empty, Escaper = "\\" },
            LocalNumbers = true,
            NumberFormat = new NumberFormatInfo { NumberGroupSeparator = ".", NumberDecimalSeparator = "," },
            Truncate = true,
            Duplicates = CsvDuplicates.Insert,
        };
        var result = await new CsvImportJob(_host, request).RunAsync(null, Ct);

        Assert.Equal(2, result.Rows);
        Assert.Equal("1|Zé|1234.56|um ; 2|ÿ€|-0.50|dois", await RowsAsync());
    }

    [Fact]
    public async Task ClienteParaNoPrimeiroErroComOsLotesAnterioresGravados()
    {
        TestServer.RequireServer();
        var sb = new StringBuilder("id;name;price;note\r\n");
        for (var i = 1; i <= 25_000; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{i};n{i};{i}.25;nota\r\n");
        }
        // Duplicate key at the end, with plain INSERT: an error after 2 batches of 10,000.
        sb.Append("5;dup;1;x\r\n");
        var path = Write("c.csv", sb.ToString(), new UTF8Encoding(false));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new CsvImportJob(_host, Request(path, CsvImportMethod.Client) with { Duplicates = CsvDuplicates.Insert }).RunAsync(null, Ct));

        Assert.Contains("Duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("record", error.Message, StringComparison.Ordinal);
        Assert.Equal("20000", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.t"));
        // The job connection had its SQL_MODE restored.
        Assert.Equal(await ScalarAsync("SELECT @@GLOBAL.sql_mode"), Convert.ToString(await _host.RunJobAsync((h, ct) => h.ExecuteScalarAsync("SELECT @@SESSION.sql_mode", ct), Ct), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task LocalInfileDesligadoNoServidorDaUmaMensagemClara()
    {
        TestServer.RequireServer();
        var path = Write("e.csv", "id;name;price;note\r\n1;a;1;x\r\n", new UTF8Encoding(false));
        await ExecAsync("SET GLOBAL local_infile = 0");
        try
        {
            var error = await Assert.ThrowsAsync<CsvLocalInfileDisabledException>(() => new CsvImportJob(_host, Request(path, CsvImportMethod.Server)).RunAsync(null, Ct));
            Assert.Contains("Client parses file contents", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ExecAsync("SET GLOBAL local_infile = 1");
        }
        // Client mode does not need it.
        Assert.Equal(1, (await new CsvImportJob(_host, Request(path, CsvImportMethod.Client)).RunAsync(null, Ct)).Rows);
    }

    [Fact]
    public async Task CancelNoModoServidor()
    {
        TestServer.RequireServer();
        var sb = new StringBuilder("id;name;price;note\r\n");
        for (var i = 1; i <= 300_000; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{i};nome {i};{i};{new string('x', 40)}\r\n");
        }
        var path = Write("d.csv", sb.ToString(), new UTF8Encoding(false));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var result = await new CsvImportJob(_host, Request(path, CsvImportMethod.Server)).RunAsync(null, cts.Token);

        Assert.True(result.Cancelled || result.Rows == 300_000);
        // The session still answers.
        Assert.Equal("1", await ScalarAsync("SELECT 1"));
    }

    [Fact]
    public async Task CancelAMeioDoLoadDataEhCancelamentoSemLinhasGravadas()
    {
        TestServer.RequireServer();
        var sb = new StringBuilder("id;name;price;note\r\n");
        for (var i = 1; i <= 300_000; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{i};nome {i};{i};{new string('x', 40)}\r\n");
        }
        var path = Write("e.csv", sb.ToString(), new UTF8Encoding(false));

        // The cancel reaches both the stream LOAD DATA reads from and MySqlConnector at the same time; when the stream wins, the
        // driver got a LOAD DATA error instead of the cancel (it failed mostly with the machine under load). Several rounds.
        for (var round = 0; round < 5; round++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            // Cancels on the first report (100 ms) with the file halfway.
            var progress = new SyncProgress<CsvImportProgress>(p =>
            {
                if (p.BytesRead > 0 && p.BytesRead < p.TotalBytes)
                {
                    cts.Cancel();
                }
            });

            var result = await new CsvImportJob(_host, Request(path, CsvImportMethod.Server)).RunAsync(progress, cts.Token);

            Assert.True(result.Cancelled);
            // The cancelled LOAD DATA leaves no half rows (InnoDB: the statement is rolled back).
            Assert.Equal("0", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.t"));
        }
        Assert.Equal("1", await ScalarAsync("SELECT 1"));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
