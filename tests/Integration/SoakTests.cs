using System.Diagnostics;
using System.Globalization;
using System.Text;
using Eunomia.App.Query;
using Eunomia.App.Transfer;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Metadata;
using Eunomia.Engine.Sql.Splitting;
using Eunomia.Engine.Transfer;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>
/// Soak (Fase 12; critério 27.3 #4) e stress B14 ao nível do motor. Soak: com <c>EUNOMIA_SOAK_MINUTES=N</c>, um ciclo
/// de queries com resultados, metadata (refresh), SQL export, Run SQL file e CSV import numa sessão durante N minutos,
/// com a memória gerida e o working set medidos a cada 30 s; a memória do fim não pode passar a do início em mais de
/// 20% + 20 MB. B14 (com <c>EUNOMIA_PERF=1</c>): 10 sessões, 5 queries longas, 2 exports e 1 import ao mesmo tempo, e
/// pedidos curtos nas ligações de metadata a medir a latência (o "utilizador a navegar"). Resultados em
/// <c>%TEMP%\eunomia-it\soak.txt</c>. As 24 h e a latência de input na UI real ficam para a máquina de referência.
/// </summary>
public sealed class SoakTests : IAsyncLifetime
{
    private readonly List<SessionHost> _hosts = [];
    private string _database = string.Empty;
    private string _directory = string.Empty;
    private string _password = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int SoakMinutes => int.TryParse(Environment.GetEnvironmentVariable("EUNOMIA_SOAK_MINUTES"), CultureInfo.InvariantCulture, out var m) ? m : 0;

    private static bool Stress => Environment.GetEnvironmentVariable("EUNOMIA_PERF") == "1";

    public async ValueTask InitializeAsync()
    {
        if (TestServer.ConnectionString is null || (SoakMinutes <= 0 && !Stress))
        {
            return;
        }
        _password = new MySqlConnectionStringBuilder(TestServer.ConnectionString).Password;
        _database = "eunomia_soak_" + Guid.NewGuid().ToString("N")[..8];
        _directory = Path.Combine(Path.GetTempPath(), "eunomia-it", "soak-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        var host = await HostAsync();
        await host.RunPrimaryAsync(async (h, ct) =>
        {
            await h.ExecuteNonQueryAsync($"CREATE DATABASE `{_database}`", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.t (id INT PRIMARY KEY, a INT, b VARCHAR(100), c DATETIME) ENGINE=InnoDB", ct);
            const string digits = "(SELECT 0 d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9)";
            await h.ExecuteNonQueryAsync(
                $"INSERT INTO `{_database}`.t SELECT n, n % 97, CONCAT('linha ', n), '2026-01-01' + INTERVAL n MINUTE FROM " +
                $"(SELECT 1 + a.d + 10 * b.d + 100 * c.d + 1000 * e.d + 10000 * f.d AS n FROM {digits} a, {digits} b, {digits} c, {digits} e, {digits} f) s WHERE n <= 50000", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.imported (id INT PRIMARY KEY, name VARCHAR(50), price DECIMAL(10,2), note TEXT)", ct);
            return true;
        }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hosts.Count > 0)
        {
            await _hosts[0].RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS `{_database}`", ct), CancellationToken.None);
        }
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<SessionHost> HostAsync()
    {
        var host = new SessionHost(new SessionConnectionSettings("soak" + _hosts.Count.ToString(CultureInfo.InvariantCulture), TestServer.Options()) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(_password),
        });
        await host.ConnectAsync(Ct);
        _hosts.Add(host);
        return host;
    }

    private static void Report(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "eunomia-it", "soak.txt"), DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
    }

    /// <summary>Uma volta do ciclo do soak.</summary>
    private async Task CycleAsync(SessionHost host, MetadataCache cache, int round)
    {
        // Query tab: resultados materializados e libertados, como ao fechar a tab.
        var runner = new QueryRunner(host);
        var results = new List<QueryResult>();
        runner.ResultCompleted += (_, r) => results.Add(r);
        var sql = $"SELECT * FROM `{_database}`.t WHERE id > {round % 1000} LIMIT 5000; SELECT COUNT(*), SUM(a) FROM `{_database}`.t";
        await runner.RunAsync(sql, SqlSplitter.Split(sql), new QueryRunOptions(), Ct);
        foreach (var r in results)
        {
            r.Source.Dispose();
        }

        // Árvore e Database tab: refresh da base e das tabelas.
        cache.Invalidate(_database);
        await cache.GetObjectsAsync(_database, Ct);
        await cache.GetTableAsync(new DbObjectRef(_database, _database, "t", DbObjectKind.Table), Ct);

        // Export para o clipboard, Run SQL file e CSV import (cliente e servidor alternados).
        var objects = (await cache.GetObjectsAsync(_database, Ct)).Objects.Where(o => o.Ref.Name == "t").ToList();
        await new SqlExportJob(host, new SqlExportRequest
        {
            Dump = new SqlDumpOptions { CreateTables = true, Data = DumpDataMode.Insert },
            Databases = [new SqlExportDatabase(_database, objects)],
            Target = SqlExportTarget.Clipboard,
            ClipboardLimit = 64L * 1024 * 1024,
        }).RunAsync(null, Ct);
        var script = Path.Combine(_directory, "script.sql");
        File.WriteAllText(script, $"DELETE FROM `{_database}`.imported;\nINSERT INTO `{_database}`.imported VALUES (1, 'a', 1, NULL), (2, 'b', 2, 'x');\n");
        await new SqlFileJob(host, new SqlFileRequest { Path = script, Database = _database }).RunAsync(null, Ct);
        var csv = Path.Combine(_directory, "data.csv");
        if (!File.Exists(csv))
        {
            var sb = new StringBuilder("id;name;price;note\r\n");
            for (var i = 1; i <= 2000; i++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{i};nome {i};{i}.5;\"nota \"\"{i}\"\"\"\r\n");
            }
            File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(false));
        }
        await new CsvImportJob(host, new CsvImportRequest
        {
            Path = csv,
            Database = _database,
            Table = "imported",
            Columns = [new("id", true), new("name", false), new("price", true), new("note", false)],
            Method = round % 2 == 0 ? CsvImportMethod.Client : CsvImportMethod.Server,
        }).RunAsync(null, Ct);
    }

    [Fact]
    public async Task SoakSemFugasDeMemoria()
    {
        Assert.SkipUnless(TestServer.ConnectionString is not null && SoakMinutes > 0, "Soak: EUNOMIA_SOAK_MINUTES=N e servidor de teste.");
        await _hosts[0].RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync("SET GLOBAL local_infile = 1", ct), Ct);
        var host = _hosts[0];
        var cache = new MetadataCache(host, new MetadataOptions());
        using var process = Process.GetCurrentProcess();
        var samples = new List<(TimeSpan At, long Managed, long WorkingSet, int Rounds)>();
        var clock = Stopwatch.StartNew();
        var nextSample = TimeSpan.Zero;
        var rounds = 0;
        while (clock.Elapsed < TimeSpan.FromMinutes(SoakMinutes))
        {
            await CycleAsync(host, cache, rounds++);
            if (clock.Elapsed >= nextSample)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                process.Refresh();
                samples.Add((clock.Elapsed, GC.GetTotalMemory(true), process.WorkingSet64, rounds));
                nextSample += TimeSpan.FromSeconds(30);
            }
        }
        var third = Math.Max(1, samples.Count / 3);
        // A primeira amostra (aquecimento: JIT, pools) não conta.
        var start = samples.Skip(1).Take(third).ToList();
        var end = samples.TakeLast(third).ToList();
        var startManaged = start.Count == 0 ? samples[0].Managed : start.Average(s => s.Managed);
        var endManaged = end.Average(s => s.Managed);
        var timeline = string.Join(" ", samples.Select(s => string.Create(CultureInfo.InvariantCulture, $"[{s.At.TotalMinutes:F1}m {s.Managed / 1048576}MB]")));
        Report(string.Create(CultureInfo.InvariantCulture,
            $"Soak {SoakMinutes} min, {rounds} voltas: gerida {startManaged / 1048576:F1} → {endManaged / 1048576:F1} MB; working set {samples[0].WorkingSet / 1048576} → {samples[^1].WorkingSet / 1048576} MB; {timeline}"));
        Assert.True(endManaged <= (startManaged * 1.2) + (20 * 1048576), $"Memória gerida a crescer: {startManaged / 1048576:F1} → {endManaged / 1048576:F1} MB");
    }

    [Fact]
    public async Task StressB14()
    {
        Assert.SkipUnless(TestServer.ConnectionString is not null && Stress, "Stress B14: EUNOMIA_PERF=1 e servidor de teste.");
        await _hosts[0].RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync("SET GLOBAL local_infile = 1", ct), Ct);
        for (var i = _hosts.Count; i < 10; i++)
        {
            await HostAsync();
        }
        var objects = (await new MetadataCache(_hosts[0], new MetadataOptions()).GetObjectsAsync(_database, Ct)).Objects.Where(o => o.Ref.Name == "t").ToList();
        var errors = new List<string>();
        async Task Guard(Func<Task> work)
        {
            try
            {
                await work();
            }
            catch (Exception e)
            {
                lock (errors)
                {
                    errors.Add(e.GetType().Name + ": " + e.Message);
                }
            }
        }

        var heavy = new List<Task>();
        for (var i = 0; i < 5; i++)
        {
            var host = _hosts[i];
            heavy.Add(Guard(() => host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync("SELECT SLEEP(4)", ct), Ct)));
        }
        for (var i = 5; i < 7; i++)
        {
            var host = _hosts[i];
            var path = Path.Combine(_directory, $"export{i}.sql");
            heavy.Add(Guard(() => new SqlExportJob(host, new SqlExportRequest
            {
                Dump = new SqlDumpOptions { CreateTables = true, Data = DumpDataMode.Insert },
                Databases = [new SqlExportDatabase(_database, objects)],
                Path = path,
            }).RunAsync(null, Ct)));
        }
        var script = Path.Combine(_directory, "stress.sql");
        var sb = new StringBuilder($"DELETE FROM `{_database}`.imported;\n");
        for (var i = 1; i <= 3000; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"INSERT INTO `{_database}`.imported VALUES ({i}, 'n{i}', {i}, NULL);\n");
        }
        File.WriteAllText(script, sb.ToString());
        heavy.Add(Guard(() => new SqlFileJob(_hosts[7], new SqlFileRequest { Path = script, Database = _database }).RunAsync(null, Ct)));

        // O "utilizador a navegar": pedidos curtos nas ligações de metadata de todas as sessões enquanto o resto corre.
        var latencies = new List<double>();
        var all = Task.WhenAll(heavy);
        var round = 0;
        while (!all.IsCompleted)
        {
            var host = _hosts[round++ % _hosts.Count];
            var clock = Stopwatch.StartNew();
            await Guard(() => host.RunMetadataAsync((h, ct) => h.ExecuteScalarAsync("SELECT 1", ct), Ct));
            latencies.Add(clock.Elapsed.TotalMilliseconds);
            await Task.Delay(20, Ct);
        }
        await all;
        latencies.Sort();
        double P(double q) => latencies[(int)Math.Min(latencies.Count - 1, Math.Ceiling(q * latencies.Count) - 1)];
        Report(string.Create(CultureInfo.InvariantCulture,
            $"B14 (motor): 10 sessões, 5 SLEEP(4), 2 exports, 1 import de 3.001 instruções; {latencies.Count} pedidos curtos: p50 {P(0.5):F1} ms, p95 {P(0.95):F1} ms, p99 {P(0.99):F1} ms, máx {latencies[^1]:F1} ms; erros {errors.Count}"));
        Assert.Empty(errors);
        Assert.True(P(0.99) < 1000, $"p99 {P(0.99):F0} ms");
    }
}
