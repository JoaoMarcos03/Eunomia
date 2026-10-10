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
/// PR-13 (SQL export vs <c>mysqldump --single-transaction --quick</c>), PR-14 (Run SQL file vs <c>mysql &lt; file</c>)
/// and PR-15 (CSV import vs <c>LOAD DATA LOCAL INFILE</c> through the <c>mysql</c> client), with XAMPP's binaries. Only with
/// <c>EUNOMIA_PERF=1</c>, in Release (<c>dotnet test -c Release</c>: in Debug our code is not optimised);
/// <c>EUNOMIA_PERF_ROWS</c> changes the size (500,000 rows by default, ~70 MB of dump). Indicative
/// values: this machine is not the reference one, and the 1 and 10 GB datasets are left for the reference machine.
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
        // In blocks of 100,000 (5 products of 10 digits; no recursive CTE, which has different limits on MySQL and MariaDB).
        const string digits = "(SELECT 0 d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9)";
        for (var start = 0; start < Rows; start += 100_000)
        {
            var numbers = $"SELECT {start + 1} + a.d + 10 * b.d + 100 * c.d + 1000 * e.d + 10000 * f.d AS n FROM {digits} a, {digits} b, {digits} c, {digits} e, {digits} f";
            var sql = "INSERT INTO `eunomia_perf_src`.big SELECT n, n * 7 % 100000, CONCAT('name ', n, ' ação'), '2026-01-01' + INTERVAL n SECOND, n / 100, REPEAT('text ''x'' ', 1 + n % 5) FROM ("
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

    /// <summary>Peak working set of the process during <paramref name="work"/> (samples every 20 ms), in MB above the start.</summary>
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
    public async Task ExportImportAndCsvAgainstTheServerTools()
    {
        Assert.SkipUnless(Enabled, "Measurement: needs EUNOMIA_PERF=1, the test server and XAMPP's mysql.exe/mysqldump.exe.");
        await CreateSourceAsync();
        var objects = await _host.RunMetadataAsync((h, ct) => h.Metadata.GetObjectsAsync("eunomia_perf_src", "eunomia_perf_src", ObjectDetail.Statistics, ct), Ct);
        var ours = Path.Combine(_directory, "ours.sql");
        var theirs = Path.Combine(_directory, "mysqldump.sql");

        // PR-13: export (structure and data, consistent snapshot, 1 MB INSERTs like mysqldump's net_buffer_length).
        var exportOurs = new List<TimeSpan>();
        var exportTheirs = new List<TimeSpan>();
        double exportPeak = 0;
        // The first run (-1) warms up the JIT, the server and the disk cache and does not count.
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
            $"PR-13: {Rows} rows; Eunomia {Median(exportOurs).TotalSeconds:F2} s ({oursMb:F0} MB, {oursMb / Median(exportOurs).TotalSeconds:F1} MB/s, peak +{exportPeak:F0} MB); mysqldump {Median(exportTheirs).TotalSeconds:F2} s ({theirsMb:F0} MB, {theirsMb / Median(exportTheirs).TotalSeconds:F1} MB/s); relative throughput {pr13 * 100:F0}%; runs {All(exportOurs)} vs {All(exportTheirs)}"));

        // PR-14: the same file (the mysqldump one) through mysql and through Run SQL file.
        var importOurs = new List<TimeSpan>();
        var importTheirs = new List<TimeSpan>();
        double importPeak = 0;
        // The first run (-1) warms up the JIT, the server and the disk cache and does not count.
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
            $"PR-14: Eunomia {Median(importOurs).TotalSeconds:F2} s (peak +{importPeak:F0} MB); mysql < file {Median(importTheirs).TotalSeconds:F2} s; relative throughput {pr14 * 100:F0}%; runs {All(importOurs)} vs {All(importTheirs)}"));

        // PR-15: CSV with HeidiSQL's separators; LOAD DATA LOCAL through the mysql client, through server mode and through client mode.
        var csv = Path.Combine(_directory, "big.csv");
        await using (var writer = new StreamWriter(csv, false, new UTF8Encoding(false), 1024 * 1024))
        {
            await writer.WriteAsync("id;a;b;c;d;e\r\n");
            for (var i = 1; i <= Rows; i++)
            {
                await writer.WriteAsync(string.Create(CultureInfo.InvariantCulture, $"{i};{i * 7 % 100000};\"name {i} ação\";2026-01-01 00:00:00;{i / 100.0:F2};\"text \"\"x\"\" {i % 5}\"\r\n"));
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
        // The first run (-1) warms up the JIT, the server and the disk cache and does not count.
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
            $"PR-15: CSV {csvMb:F0} MB; mysql LOAD DATA LOCAL {Median(csvTheirs).TotalSeconds:F2} s; Eunomia server {Median(csvServer).TotalSeconds:F2} s ({Median(csvTheirs) / Median(csvServer) * 100:F0}%), client {Median(csvClient).TotalSeconds:F2} s; peak +{csvPeak:F0} MB; runs {All(csvServer)} vs {All(csvTheirs)}"));
    }
}
