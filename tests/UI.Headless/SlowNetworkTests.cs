using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Eunomia.App.Settings;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Shell;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Medição com um servidor lento pela rede (proxy a 256 KB/s com 60 ms por pedido; feedback da beta, 9 out 2026): a UI
/// thread não pára enquanto chega uma página grande, e escolher outra coisa a meio cancela a carga. Só corre com
/// <c>EUNOMIA_PERF=1</c>; resultados em <c>%TEMP%\eunomia-slow-network.txt</c>.
/// </summary>
public sealed class SlowNetworkTests
{
    private const string Digits = "(SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9)";

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(5);

    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    /// <summary>Maior intervalo entre duas voltas do ciclo de UI (um stall da UI thread aparece aqui).</summary>
    private static TimeSpan _longestGap;

    /// <summary>Corre a UI até <paramref name="condition"/>; devolve quanto demorou, ou <c>null</c> se não chegou lá.</summary>
    private static async Task<TimeSpan?> PumpAsync(Func<bool> condition, TimeSpan timeout)
    {
        var total = Stopwatch.StartNew();
        var last = Stopwatch.GetTimestamp();
        while (total.Elapsed < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            var gap = Stopwatch.GetElapsedTime(last);
            if (gap > _longestGap)
            {
                _longestGap = gap;
            }
            if (condition())
            {
                return total.Elapsed;
            }
            await Task.Delay(10);
            last = Stopwatch.GetTimestamp();
        }
        return null;
    }

    private static async Task<TimeSpan> UntilAsync(Func<bool> condition, TimeSpan timeout) =>
        await PumpAsync(condition, timeout) ?? throw new TimeoutException("Condition not reached.");

    private static Task PauseAsync(TimeSpan duration) => PumpAsync(() => false, duration);

    [AvaloniaFact(Timeout = UiTimeouts.Measurement)]
    public async Task UiFluidaECargaCanceladaComRedeLenta()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null || Environment.GetEnvironmentVariable("EUNOMIA_PERF") != "1", "Medição: EUNOMIA_PERF=1 e servidor de teste.");
        var db = NewDatabaseName("eunomia_net_");
        var db2 = NewDatabaseName("eunomia_net2_");
        // 20.000 linhas de 1,6 KB: a primeira página (1.000 linhas) leva uns 8 s pelo proxy; Show all, minutos.
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE DATABASE `{db2}`; " +
            $"CREATE TABLE `{db}`.big (id INT PRIMARY KEY, c1 VARCHAR(255), c2 VARCHAR(255), c3 VARCHAR(255), c4 VARCHAR(255), c5 VARCHAR(255), c6 VARCHAR(255), c7 VARCHAR(255), c8 VARCHAR(255)) ENGINE=InnoDB; " +
            $"INSERT INTO `{db}`.big SELECT a.n + 10 * b.n + 100 * c.n + 1000 * d.n + 10000 * e.n, REPEAT('a', 200), REPEAT('b', 200), REPEAT('c', 200), REPEAT('d', 200), REPEAT('e', 200), REPEAT('f', 200), REPEAT('g', 200), REPEAT('h', 200) " +
            $"FROM {Digits} a, {Digits} b, {Digits} c, {Digits} d, (SELECT 0 n UNION ALL SELECT 1) e; " +
            $"CREATE TABLE `{db}`.small (id INT PRIMARY KEY); INSERT INTO `{db}`.small VALUES (1), (2), (3); " +
            $"CREATE TABLE `{db2}`.other (id INT PRIMARY KEY); INSERT INTO `{db2}`.other VALUES (1), (2);");
        var server = new MySqlConnectionStringBuilder(cs);
        await using var proxy = new SlowProxy(server.Server, (int)server.Port, bytesPerSecond: 256 * 1024, latency: TimeSpan.FromMilliseconds(60));
        var slow = new MySqlConnectionStringBuilder(cs) { Port = (uint)proxy.Port }.ConnectionString;
        try
        {
            var (window, context, session, connector, _) = CreateShell(slow, $"{db};{db2}");
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault(), TimeSpan.FromSeconds(30));
            await WaitAsync(() => root.Children?.Count == 2 ? "ok" : null, TimeSpan.FromSeconds(30));
            var dbNode = root.Children!.Single(n => n.Name == db);
            var db2Node = root.Children!.Single(n => n.Name == db2);
            await model.ExpandAsync(dbNode);
            await model.ExpandAsync(db2Node);
            var host = connector.Open.Single().Host;
            window.ShowDataTab();
            var report = new List<string>();

            bool Showing(string name, int rows) => window.DataBrowser?.Table.Ref.Name == name && window.DataGrid.RowCount == rows && !host.IsPrimaryBusy;

            // A meio da primeira página da grande (1 s depois de começar), escolhe outro nó.
            async Task<TimeSpan> MeasureAsync(string what, TreeNode next, Func<bool> done)
            {
                // Sai da grande (se for a do Data tab, focá-la de novo não recarregava) e volta a ela.
                window.ShowDataTab();
                window.Tree.Tree.FocusedNode = Child(dbNode, "small");
                await UntilAsync(() => Showing("small", 3), TimeSpan.FromMinutes(1));
                window.Tree.Tree.FocusedNode = Child(dbNode, "big");
                await UntilAsync(() => host.IsPrimaryBusy, TimeSpan.FromSeconds(30));
                await PauseAsync(TimeSpan.FromSeconds(1));
                window.Tree.Tree.FocusedNode = next;
                var elapsed = await UntilAsync(done, TimeSpan.FromMinutes(3));
                report.Add(string.Create(CultureInfo.InvariantCulture, $"{what}: {elapsed.TotalMilliseconds:0} ms"));
                await UntilAsync(() => !host.IsPrimaryBusy, TimeSpan.FromMinutes(3));
                return elapsed;
            }

            // Primeira página inteira: a UI thread nunca fica parada.
            _longestGap = TimeSpan.Zero;
            window.Tree.Tree.FocusedNode = Child(dbNode, "big");
            var page = await UntilAsync(() => Showing("big", 1000), TimeSpan.FromMinutes(2));
            report.Add(string.Create(CultureInfo.InvariantCulture, $"big first page: {page.TotalMilliseconds:0} ms; longest UI gap {_longestGap.TotalMilliseconds:0} ms"));
            Assert.True(_longestGap < UiStallMonitor.Threshold, $"UI thread stalled {_longestGap.TotalMilliseconds:0} ms");

            // Escolher outra coisa a meio da página.
            var sameDatabase = await MeasureAsync("other table, same database", Child(dbNode, "small"), () => Showing("small", 3));
            var otherDatabase = await MeasureAsync("table in another database", Child(db2Node, "other"), () => Showing("other", 2));
            var databaseNode = await MeasureAsync("database node (primary free)", dbNode, () => !host.IsPrimaryBusy);

            // Show all a meio e outra tabela. (Do nó da base, a tabela abre no Table tab.)
            window.Tree.Tree.FocusedNode = Child(dbNode, "big");
            window.ShowDataTab();
            await UntilAsync(() => Showing("big", 1000), TimeSpan.FromMinutes(2));
            var all = context.Commands.ExecuteAsync("data.showAll").AsTask();
            await UntilAsync(() => host.IsPrimaryBusy, TimeSpan.FromSeconds(30));
            await PauseAsync(TimeSpan.FromSeconds(1));
            window.Tree.Tree.FocusedNode = Child(dbNode, "small");
            var showAll = await UntilAsync(() => Showing("small", 3), TimeSpan.FromMinutes(3));
            await all;
            report.Add(string.Create(CultureInfo.InvariantCulture, $"other table during Show all: {showAll.TotalMilliseconds:0} ms; longest UI gap {_longestGap.TotalMilliseconds:0} ms"));
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "eunomia-slow-network.txt"), report);

            Assert.True(sameDatabase < Quick && otherDatabase < Quick && databaseNode < Quick && showAll < Quick, string.Join("; ", report));
            Assert.True(_longestGap < UiStallMonitor.Threshold, $"UI thread stalled {_longestGap.TotalMilliseconds:0} ms");

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`; DROP DATABASE IF EXISTS `{db2}`");
        }
    }

    /// <summary>
    /// Idas ao servidor e tempo das ações do dia a dia com 50 ms por pedido (um servidor noutra cidade, ou uma VPN):
    /// ligar, abrir uma base, abrir uma tabela no Data tab e outra no Table tab. Resultados em
    /// <c>%TEMP%\eunomia-roundtrips.txt</c>.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Measurement)]
    public async Task IdasAoServidorPorAcao()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null || Environment.GetEnvironmentVariable("EUNOMIA_PERF") != "1", "Medição: EUNOMIA_PERF=1 e servidor de teste.");
        var db = NewDatabaseName("eunomia_rt_");
        var tables = string.Concat(Enumerable.Range(1, 30).Select(i => $"CREATE TABLE `{db}`.t{i:D2} (id INT PRIMARY KEY, name VARCHAR(40)); "));
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; {tables}" +
            $"CREATE TABLE `{db}`.parent (id INT PRIMARY KEY, name VARCHAR(40), UNIQUE KEY uq_name (name)); " +
            $"CREATE TABLE `{db}`.child (id INT PRIMARY KEY, parent_id INT, note TEXT, KEY ix_parent (parent_id), CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES `{db}`.parent (id)); " +
            $"INSERT INTO `{db}`.parent VALUES (1, 'a'), (2, 'b'); INSERT INTO `{db}`.child VALUES (1, 1, 'x'), (2, 2, 'y'), (3, 1, 'z');");
        var server = new MySqlConnectionStringBuilder(cs);
        await using var proxy = new SlowProxy(server.Server, (int)server.Port, bytesPerSecond: 4 * 1024 * 1024, latency: TimeSpan.FromMilliseconds(50));
        var slow = new MySqlConnectionStringBuilder(cs) { Port = (uint)proxy.Port }.ConnectionString;
        try
        {
            var settings = new AppSettings();
            settings.Log.Timestamps = true;
            settings.Log.MaxLines = 5000;
            var (window, _, session, connector, log) = CreateShell(slow, db, settings);
            var report = new List<string>();

            // Mede uma ação: tempo até "done" e pedidos ao servidor até tudo acalmar (1 s sem pedidos novos).
            async Task MeasureAsync(string what, Action start, Func<bool> done)
            {
                var before = proxy.Requests;
                var firstLine = log.Snapshot().LastOrDefault()?.Number ?? 0;
                var clock = Stopwatch.StartNew();
                start();
                var elapsed = await UntilAsync(done, TimeSpan.FromMinutes(1));
                var seen = proxy.Requests;
                var quiet = Stopwatch.StartNew();
                while (quiet.Elapsed < TimeSpan.FromSeconds(1))
                {
                    await PauseAsync(TimeSpan.FromMilliseconds(100));
                    if (proxy.Requests != seen)
                    {
                        seen = proxy.Requests;
                        quiet.Restart();
                    }
                }
                report.Add(string.Create(CultureInfo.InvariantCulture, $"{what}: {elapsed.TotalMilliseconds:0} ms, {seen - before} requests"));
                report.AddRange(log.Snapshot().Where(l => l.Number > firstLine).Select(l => "    " + l.Text));
            }

            var model = window.Tree.Tree.Model!;
            Task<bool>? connecting = null;
            await MeasureAsync("connect until databases listed", () => connecting = window.ConnectAsync(window, session, null),
                () => model.Roots.FirstOrDefault()?.Children?.Count > 0);
            Assert.True(await connecting!);
            var dbNode = model.Roots[0].Children!.Single(n => n.Name == db);
            Task? expanding = null;
            await MeasureAsync("expand database (32 tables)", () => expanding = model.ExpandAsync(dbNode), () => expanding!.IsCompleted && dbNode.Children?.Count > 0);
            var host = connector.Open.Single().Host;

            window.ShowDataTab();
            await MeasureAsync("table in Data tab (child)", () => window.Tree.Tree.FocusedNode = dbNode.Children!.Single(n => n.Name == "child"),
                () => window.DataBrowser?.Table.Ref.Name == "child" && window.DataGrid.RowCount == 3 && !host.IsPrimaryBusy);
            await MeasureAsync("other table in Data tab (parent)", () => window.Tree.Tree.FocusedNode = dbNode.Children!.Single(n => n.Name == "parent"),
                () => window.DataBrowser?.Table.Ref.Name == "parent" && window.DataGrid.RowCount == 2 && !host.IsPrimaryBusy);
            await MeasureAsync("back to a table already seen (child)", () => window.Tree.Tree.FocusedNode = dbNode.Children!.Single(n => n.Name == "child"),
                () => window.DataBrowser?.Table.Ref.Name == "child" && window.DataGrid.RowCount == 3 && !host.IsPrimaryBusy);

            // Setas pela árvore: 10 tabelas, uma a cada 120 ms; mede até a última aparecer.
            var tenTables = Enumerable.Range(11, 10).Select(i => dbNode.Children!.Single(n => n.Name == $"t{i:D2}")).ToList();
            await MeasureAsync("arrow keys over 10 tables in Data tab (120 ms apart)", () =>
            {
                _ = Task.Run(async () =>
                {
                    foreach (var node in tenTables)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => window.Tree.Tree.FocusedNode = node);
                        await Task.Delay(120);
                    }
                });
            }, () => window.DataBrowser?.Table.Ref.Name == "t20" && window.DataGrid.RowCount == 0 && !host.IsPrimaryBusy && window.DataBrowser.Source is not null);

            window.Tabs.SelectedItem = window.Tabs.TableTab;
            await MeasureAsync("table in Table tab (t01)", () => window.Tree.Tree.FocusedNode = dbNode.Children!.Single(n => n.Name == "t01"),
                () => (window.Tabs.TableTab.Content as UI.Features.TableEditor.TableEditorView)?.Design.Name == "t01");
            await MeasureAsync("other table in Table tab (t02)", () => window.Tree.Tree.FocusedNode = dbNode.Children!.Single(n => n.Name == "t02"),
                () => (window.Tabs.TableTab.Content as UI.Features.TableEditor.TableEditorView)?.Design.Name == "t02");

            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "eunomia-roundtrips.txt"), report);
            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    /// <summary>Proxy TCP com largura de banda limitada (servidor → cliente) e um atraso fixo em cada pedido do cliente.</summary>
    private sealed class SlowProxy : IAsyncDisposable
    {
        private int _requests;

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _host;
        private readonly int _port;
        private readonly int _bytesPerSecond;
        private readonly TimeSpan _latency;
        private readonly Task _accept;

        public SlowProxy(string host, int port, int bytesPerSecond, TimeSpan latency)
        {
            _host = host;
            _port = port;
            _bytesPerSecond = bytesPerSecond;
            _latency = latency;
            _listener.Start();
            _accept = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>Blocos enviados pelo cliente (cada comando do protocolo é um; pedidos seguidos podem juntar-se).</summary>
        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    break;
                }
                _ = ForwardAsync(client);
            }
        }

        private async Task ForwardAsync(TcpClient client)
        {
            using (client)
            using (var server = new TcpClient())
            {
                await server.ConnectAsync(_host, _port);
                // Buffers pequenos: o que o servidor já mandou e o cliente ainda não leu não pode ser muito.
                client.ReceiveBufferSize = server.ReceiveBufferSize = 16 * 1024;
                var up = PumpAsync(client.GetStream(), server.GetStream(), throttle: false);
                var down = PumpAsync(server.GetStream(), client.GetStream(), throttle: true);
                await Task.WhenAny(up, down);
            }
        }

        private async Task PumpAsync(NetworkStream from, NetworkStream to, bool throttle)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    var n = await from.ReadAsync(buffer, _stop.Token);
                    if (n == 0)
                    {
                        break;
                    }
                    if (!throttle)
                    {
                        Interlocked.Increment(ref _requests);
                    }
                    await Task.Delay(throttle ? TimeSpan.FromSeconds((double)n / _bytesPerSecond) : _latency, _stop.Token);
                    await to.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // Ligação fechada de um dos lados.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accept;
            _stop.Dispose();
        }
    }
}
