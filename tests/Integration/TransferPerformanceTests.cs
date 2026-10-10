using System.Diagnostics;
using System.Globalization;
using System.Text;
using Eunomia.App.Transfer;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Transfer;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>
/// PR-13 (SQL export vs <c>mysqldump --single-transaction --quick</c>), PR-14 (Run SQL file vs <c>mysql &lt; ficheiro</c>)
/// e PR-15 (CSV import vs <c>LOAD DATA LOCAL INFILE</c> pelo cliente <c>mysql</c>), com os binários do XAMPP. Só com
/// <c>EUNOMIA_PERF=1</c>, em Release (<c>dotnet test -c Release</c>: em Debug o nosso código não é otimizado);
/// <c>EUNOMIA_PERF_ROWS</c> muda o tamanho (500.000 linhas por omissão, ~70 MB de dump). Valores
/// indicativos: esta máquina não é a de referência e os datasets de 1 e 10 GB ficam para o Marcos.
/// </summary>
public sealed class TransferPerformanceTests : IAsyncLifetime
{
    private const int Runs = 3;
    private SessionHost _host = null!;
    private string _directory = string.Empty;
    private string _password = string.Empty;
    private MySqlConnectionStringBuilder _builder = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Enabled => TestServer.ConnectionString is not null && Environment.GetEnvironmentVariable("EUNOMIA_PERF") == "1" && ClientTool("mysql") is not null;

    private static int Rows => int.TryParse(Environment.GetEnvironmentVariable("EUNOMIA_PERF_ROWS"), CultureInfo.InvariantCulture, out var rows) ? rows : 500_000;

    private static string? ClientTool(string name)
    {
        foreach (var dir in new[] { Environment.GetEnvironmentVariable("EUNOMIA_MYSQL_BIN"), @"E:\xampp\mysql\bin", @"C:\xampp\mysql\bin" })
        {
            if (dir is { Length: > 0 } && File.Exists(Path.Combine(dir, name + ".exe")))
            {
                return Path.Combine(dir, name + ".exe");
            }
        }
        return null;
    }

    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }
        _builder = new MySqlConnectionStringBuilder(TestServer.ConnectionString!);
        _password = _builder.Password;
        _host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(_password),
        });
        await _host.ConnectAsync(Ct);
        _directory = Path.Combine(Path.GetTempPath(), "eunomia-it", "perf-transfer");
        Directory.CreateDirectory(_directory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            foreach (var db in new[] { "eunomia_perf_src", "eunomia_perf_dst" })
            {
                await ExecAsync($"DROP DATABASE IF EXISTS `{db}`");
            }
            await _host.DisposeAsync();
        }
    }

    private Task ExecAsync(string sql) => _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync(sql, ct), CancellationToken.None);

    private async Task<long> CountAsync(string table) =>
        Convert.ToInt64(await _host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync($"SELECT COUNT(*) FROM {table}", ct), Ct), CultureInfo.InvariantCulture);

    private async Task CreateSourceAsync()
    {
        await ExecAsync("DROP DATABASE IF EXISTS `eunomia_perf_src`");
        await ExecAsync("CREATE DATABASE `eunomia_perf_src` CHARACTER SET utf8mb4");
        await ExecAsync("CREATE TABLE `eunomia_perf_src`.big (id INT PRIMARY KEY, a INT, b VARCHAR(64), c DATETIME, d DECIMAL(10,2), e TEXT) ENGINE=InnoDB");
        // Em blocos de 100.000 (5 produtos de 10 dígitos; sem CTE recursivo, que tem limites diferentes em MySQL e MariaDB).
        const string digits = "(SELECT 0 d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9)";
        for (var start = 0; start < Rows; start += 100_000)
        {
            var numbers = $"SELECT {start + 1} + a.d + 10 * b.d + 100 * c.d + 1000 * e.d + 10000 * f.d AS n FROM {digits} a, {digits} b, {digits} c, {digits} e, {digits} f";
            var sql = "INSERT INTO `eunomia_perf_src`.big SELECT n, n * 7 % 100000, CONCAT('nome ', n, ' ação'), '2026-01-01' + INTERVAL n SECOND, n / 100, REPEAT('texto ''x'' ', 1 + n % 5) FROM ("
                + numbers + ") s WHERE n <= " + Rows.ToString(CultureInfo.InvariantCulture);
            await ExecAsync(sql);
        }
    }

    private async Task<(TimeSpan Elapsed, int ExitCode, string Error)> RunToolAsync(string tool, string arguments, string? stdinFile = null)
    {
        var info = new ProcessStartInfo(ClientTool(tool)!, arguments)
        {
            RedirectStandardInput = stdinFile is not null,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["MYSQL_PWD"] = _password;
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        if (stdinFile is not null)
        {
            await using (var input = File.OpenRead(stdinFile))
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, 1024 * 1024, Ct);
            }
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync(Ct);
        await stdout;
        return (clock.Elapsed, process.ExitCode, await stderr);
    }

    private string Connection => string.Create(CultureInfo.InvariantCulture, $"-h {_builder.Server} -P {_builder.Port} -u {_builder.UserID}");

    /// <summary>Pico do working set do processo durante <paramref name="work"/> (amostras a cada 20 ms), em MB acima do início.</summary>
    private static async Task<(TimeSpan Elapsed, double PeakMb)> MeasureAsync(Func<Task> work)
    {
        using var process = Process.GetCurrentProcess();
        GC.Collect();
        process.Refresh();
        var baseline = process.WorkingSet64;
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                await Task.Delay(20, CancellationToken.None);
            }
        });
        var clock = Stopwatch.StartNew();
        await work();
        clock.Stop();
        await stop.CancelAsync();
        await sampler;
        return (clock.Elapsed, (peak - baseline) / (1024.0 * 1024));
    }

    private static void Report(string line)
    {
        TestContext.Current.TestOutputHelper!.WriteLine(line);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "eunomia-it", "perf-transfer.txt"), DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
    }

    private static string All(IEnumerable<TimeSpan> values) => string.Join("/", values.Select(v => v.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)));

    private static TimeSpan Median(IEnumerable<TimeSpan> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    [Fact]
    public async Task ExportImportECsvContraAsFerramentasDoServidor()
    {
        Assert.SkipUnless(Enabled, "Medição: EUNOMIA_PERF=1, servidor de teste e mysql.exe/mysqldump.exe do XAMPP.");
        await CreateSourceAsync();
        var objects = await _host.RunMetadataAsync((h, ct) => h.Metadata.GetObjectsAsync("eunomia_perf_src", "eunomia_perf_src", ObjectDetail.Statistics, ct), Ct);
        var ours = Path.Combine(_directory, "ours.sql");
        var theirs = Path.Combine(_directory, "mysqldump.sql");

        // PR-13: export (estrutura e dados, snapshot consistente, INSERT de 1 MB como o net_buffer_length do mysqldump).
        var exportOurs = new List<TimeSpan>();
        var exportTheirs = new List<TimeSpan>();
        double exportPeak = 0;
        // A primeira corrida (-1) aquece o JIT, o servidor e a cache do disco e não conta.
        for (var run = -1; run < Runs; run++)
        {
            var request = new SqlExportRequest
            {
                Dump = new SqlDumpOptions { CreateTables = true, DropTables = true, Data = DumpDataMode.Insert, MaxInsertSizeKb = 1024 },
                Databases = [new SqlExportDatabase("eunomia_perf_src", objects)],
                Target = SqlExportTarget.File,
                Path = ours,
                ConsistentSnapshot = true,
            };
            var (elapsed, peak) = await MeasureAsync(() => new SqlExportJob(_host, request).RunAsync(null, Ct));
            if (run >= 0)
            {
                exportOurs.Add(elapsed);
                exportPeak = Math.Max(exportPeak, peak);
            }
            var tool = await RunToolAsync("mysqldump", $"{Connection} --single-transaction --quick --skip-lock-tables --net-buffer-length=1048576 --result-file=\"{theirs}\" eunomia_perf_src big");
            Assert.True(tool.ExitCode == 0, tool.Error);
            if (run >= 0)
            {
                exportTheirs.Add(tool.Elapsed);
            }
        }
        var oursMb = new FileInfo(ours).Length / (1024.0 * 1024);
        var theirsMb = new FileInfo(theirs).Length / (1024.0 * 1024);
        var pr13 = Median(exportTheirs) / Median(exportOurs);
        Report(string.Create(CultureInfo.InvariantCulture,
            $"PR-13: {Rows} linhas; Eunomia {Median(exportOurs).TotalSeconds:F2} s ({oursMb:F0} MB, {oursMb / Median(exportOurs).TotalSeconds:F1} MB/s, pico +{exportPeak:F0} MB); mysqldump {Median(exportTheirs).TotalSeconds:F2} s ({theirsMb:F0} MB, {theirsMb / Median(exportTheirs).TotalSeconds:F1} MB/s); débito relativo {pr13 * 100:F0}%; corridas {All(exportOurs)} vs {All(exportTheirs)}"));

        // PR-14: o mesmo ficheiro (o do mysqldump) pelo mysql e pelo Run SQL file.
        var importOurs = new List<TimeSpan>();
        var importTheirs = new List<TimeSpan>();
        double importPeak = 0;
        // A primeira corrida (-1) aquece o JIT, o servidor e a cache do disco e não conta.
        for (var run = -1; run < Runs; run++)
        {
            await ExecAsync("DROP DATABASE IF EXISTS `eunomia_perf_dst`");
            await ExecAsync("CREATE DATABASE `eunomia_perf_dst` CHARACTER SET utf8mb4");
            var tool = await RunToolAsync("mysql", $"{Connection} eunomia_perf_dst", theirs);
            Assert.True(tool.ExitCode == 0, tool.Error);
            if (run >= 0)
            {
                importTheirs.Add(tool.Elapsed);
            }
            Assert.Equal(Rows, await CountAsync("`eunomia_perf_dst`.big"));

            await ExecAsync("DROP DATABASE IF EXISTS `eunomia_perf_dst`");
            await ExecAsync("CREATE DATABASE `eunomia_perf_dst` CHARACTER SET utf8mb4");
            SqlFileResult? result = null;
            var (elapsed, peak) = await MeasureAsync(async () => result = await new SqlFileJob(_host, new SqlFileRequest { Path = theirs, Database = "eunomia_perf_dst" }).RunAsync(null, Ct));
            Assert.True(result!.Completed && result.Errors == 0);
            if (run >= 0)
            {
                importOurs.Add(elapsed);
                importPeak = Math.Max(importPeak, peak);
            }
            Assert.Equal(Rows, await CountAsync("`eunomia_perf_dst`.big"));
        }
        var pr14 = Median(importTheirs) / Median(importOurs);
        Report(string.Create(CultureInfo.InvariantCulture,
            $"PR-14: Eunomia {Median(importOurs).TotalSeconds:F2} s (pico +{importPeak:F0} MB); mysql < ficheiro {Median(importTheirs).TotalSeconds:F2} s; débito relativo {pr14 * 100:F0}%; corridas {All(importOurs)} vs {All(importTheirs)}"));

        // PR-15: CSV com os separadores do HeidiSQL; LOAD DATA LOCAL pelo cliente mysql, pelo modo servidor e pelo modo cliente.
        var csv = Path.Combine(_directory, "big.csv");
        await using (var writer = new StreamWriter(csv, false, new UTF8Encoding(false), 1024 * 1024))
        {
            await writer.WriteAsync("id;a;b;c;d;e\r\n");
            for (var i = 1; i <= Rows; i++)
            {
                await writer.WriteAsync(string.Create(CultureInfo.InvariantCulture, $"{i};{i * 7 % 100000};\"nome {i} ação\";2026-01-01 00:00:00;{i / 100.0:F2};\"texto \"\"x\"\" {i % 5}\"\r\n"));
            }
        }
        var csvMb = new FileInfo(csv).Length / (1024.0 * 1024);
        await ExecAsync("DROP DATABASE IF EXISTS `eunomia_perf_dst`");
        await ExecAsync("CREATE DATABASE `eunomia_perf_dst` CHARACTER SET utf8mb4");
        await ExecAsync("CREATE TABLE `eunomia_perf_dst`.big LIKE `eunomia_perf_src`.big");
        CsvTargetColumn[] columns = [new("id", true), new("a", true), new("b", false), new("c", false), new("d", true), new("e", false)];
        var csvTheirs = new List<TimeSpan>();
        var csvServer = new List<TimeSpan>();
        var csvClient = new List<TimeSpan>();
        double csvPeak = 0;
        var load = "LOAD DATA LOCAL INFILE '" + csv.Replace("\\", "/", StringComparison.Ordinal) + "' REPLACE INTO TABLE big CHARACTER SET utf8mb4 FIELDS TERMINATED BY ';' OPTIONALLY ENCLOSED BY '\\\"' ESCAPED BY '\\\"' LINES TERMINATED BY '\\r\\n' IGNORE 1 LINES";
        // A primeira corrida (-1) aquece o JIT, o servidor e a cache do disco e não conta.
        for (var run = -1; run < Runs; run++)
        {
            await ExecAsync("TRUNCATE TABLE `eunomia_perf_dst`.big");
            var tool = await RunToolAsync("mysql", $"{Connection} --local-infile=1 eunomia_perf_dst -e \"{load}\"");
            Assert.True(tool.ExitCode == 0, tool.Error);
            if (run >= 0)
            {
                csvTheirs.Add(tool.Elapsed);
            }
            Assert.Equal(Rows, await CountAsync("`eunomia_perf_dst`.big"));

            foreach (var (method, times) in new[] { (CsvImportMethod.Server, csvServer), (CsvImportMethod.Client, csvClient) })
            {
                await ExecAsync("TRUNCATE TABLE `eunomia_perf_dst`.big");
                var request = new CsvImportRequest { Path = csv, Database = "eunomia_perf_dst", Table = "big", Columns = columns, Method = method, LowPriority = false };
                var (elapsed, peak) = await MeasureAsync(() => new CsvImportJob(_host, request).RunAsync(null, Ct));
                if (run >= 0)
                {
                    times.Add(elapsed);
                    csvPeak = Math.Max(csvPeak, peak);
                }
                Assert.Equal(Rows, await CountAsync("`eunomia_perf_dst`.big"));
            }
        }
        Report(string.Create(CultureInfo.InvariantCulture,
            $"PR-15: CSV {csvMb:F0} MB; mysql LOAD DATA LOCAL {Median(csvTheirs).TotalSeconds:F2} s; Eunomia servidor {Median(csvServer).TotalSeconds:F2} s ({Median(csvTheirs) / Median(csvServer) * 100:F0}%), cliente {Median(csvClient).TotalSeconds:F2} s; pico +{csvPeak:F0} MB; corridas {All(csvServer)} vs {All(csvTheirs)}"));
    }
}
