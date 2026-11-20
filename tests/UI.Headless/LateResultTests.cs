using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.Infra.Settings;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.QueryTab;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Late results and repeated commands (code audit, Nov 2026): a load that answers after another choice does not overwrite
/// what is shown, a result tab's filter belongs to that tab, secrets stay out of the history and F9 twice runs once.
/// </summary>
public sealed class LateResultTests
{
    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    /// <summary>Lets the UI run for a while (late answers arrive and their continuations run).</summary>
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task HostTabIgnoresTheLateDatabasesOfTheSessionLeft()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var builder = new MySqlConnectionStringBuilder(cs);
        var dbA = NewDatabaseName("eunomia_lra_");
        var dbB = NewDatabaseName("eunomia_lrb_");
        await ExecAsync(cs, $"CREATE DATABASE `{dbA}`; CREATE DATABASE `{dbB}`;");
        await using var proxy = new GatedProxy(builder.Server, (int)builder.Port);
        try
        {
            // Session A through the gate, session B straight to the server; each one sees only its own database.
            var (window, context, a, connector, _) = CreateShell(cs, dbA);
            a.Host = "127.0.0.1";
            a.Port = proxy.Port;
            var b = new SessionDefinition { Name = "b", Host = builder.Server, Port = (int)builder.Port, User = builder.UserID, PasswordSaved = true, Databases = dbB };
            context.Sessions!.Catalog.Sessions.Add(b);
            context.Sessions.Secrets.Set(SecretKeys.Password(b.Id), builder.Password);
            Assert.True(await window.ConnectAsync(window, a, null));
            Assert.True(await window.ConnectAsync(window, b, null));
            var model = window.Tree.Tree.Model!;
            var rootA = await WaitAsync(() => model.Roots.FirstOrDefault(n => n.Tag is Guid id && id == a.Id));
            var rootB = await WaitAsync(() => model.Roots.FirstOrDefault(n => n.Tag is Guid id && id == b.Id));
            await WaitAsync(() => rootA.Children?.FirstOrDefault());
            await WaitAsync(() => rootB.Children?.FirstOrDefault());
            var host = window.HostView;
            window.Tree.Tree.FocusedNode = rootB;
            await WaitAsync(() => host.DatabaseList.Roots.FirstOrDefault(n => n[0] == dbB));

            // A's list has to come from the server again, and its answer is held back: B is chosen meanwhile.
            var openA = connector.Find(a.Id)!;
            openA.Metadata.Invalidate();
            proxy.Pause();
            window.Tree.Tree.FocusedNode = rootA;
            Dispatcher.UIThread.RunJobs();
            window.Tree.Tree.FocusedNode = rootB;
            await WaitAsync(() => host.DatabaseList.Roots.FirstOrDefault(n => n[0] == dbB));
            proxy.Resume();
            await openA.Metadata.GetDatabaseCollationsAsync(CancellationToken.None);
            await SettleAsync();

            Assert.Equal([dbB], host.DatabaseList.Roots.Select(n => n[0]));
            window.Close();
        }
        finally
        {
            proxy.Resume();
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{dbA}`; DROP DATABASE IF EXISTS `{dbB}`;");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task TableEditorDiscardReloadDoesNotComeBackAfterAnotherChoice()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var builder = new MySqlConnectionStringBuilder(cs);
        var db = NewDatabaseName("eunomia_lrt_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t1 (id INT PRIMARY KEY);");
        await using var proxy = new GatedProxy(builder.Server, (int)builder.Port);
        try
        {
            var (window, context, session, connector, _) = CreateShell(cs, db);
            context.Settings.Tree.OpenTablesInDataTab = false;
            session.Host = "127.0.0.1";
            session.Port = proxy.Port;
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);
            window.Tree.Tree.FocusedNode = Child(dbNode, "t1");
            var editor = await WaitAsync(() => window.TableEditor);

            // Discard reads the table again; its answer is held back while the database is chosen in the tree.
            proxy.Pause();
            var discard = editor.GetLogicalDescendants().OfType<Button>().Single(x => Equals(x.Content, "Discard"));
            discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.Tree.Tree.FocusedNode = dbNode;
            await WaitAsync(() => ReferenceEquals(window.Tabs.SelectedItem, window.Tabs.DatabaseTab) ? "ok" : null);
            proxy.Resume();
            await connector.Find(session.Id)!.Metadata.GetObjectsAsync(db, CancellationToken.None);
            await SettleAsync();

            // The reload was cancelled by the choice: the Table tab still holds the editor it had, not a new one.
            Assert.Same(editor, window.TableEditor);
            window.Close();
        }
        finally
        {
            proxy.Resume();
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ResultTabFilterBelongsToItsTabAndARunStartsClean()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_lrf_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t (id INT PRIMARY KEY, name VARCHAR(20)); INSERT INTO `{db}`.t VALUES (1, 'ana'), (2, 'rui'), (3, 'eva');");
        try
        {
            var (window, context, session, _, _) = CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[0].Content);
            window.SqlEditor.Text = $"SELECT * FROM `{db}`.t;\nSELECT * FROM `{db}`.t;";
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            Assert.Equal(2, view.ResultGrids.Count);

            // Result 1 filtered; result 2 has its own (empty) box; back on result 1, its text and rows come back.
            view.Results.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            window.LocalFilterBox.Text = "rui";
            await window.ApplyLocalFilterAsync();
            await WaitAsync(() => view.ResultGrids[0].Control.RowCount == 1 ? "ok" : null);
            view.Results.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(string.Empty, window.LocalFilterBox.Text);
            Assert.Equal(3, view.ResultGrids[1].Control.RowCount);
            view.Results.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("rui", window.LocalFilterBox.Text);
            Assert.Equal(1, view.ResultGrids[0].Control.RowCount);

            // A new run replaces the results: no filter text and every row.
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            await WaitAsync(() => view.ResultGrids.Count == 2 && view.ResultGrids[0].Control.RowCount == 3 ? "ok" : null);
            view.Results.SelectedIndex = 0;
            await SettleAsync();
            Assert.Equal(string.Empty, window.LocalFilterBox.Text);
            Assert.Equal(3, view.ResultGrids[0].Control.RowCount);
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task CreateUserWithPasswordStaysOutOfTheHistory()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_lrh_");
        var user = NewDatabaseName("eunomia_u");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`");
        var dir = Path.Combine(Path.GetTempPath(), "eunomia-lrh-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var appDb = new SqliteAppDatabase(Path.Combine(dir, "app.db"));
            var (window, context, session, _, _) = CreateShell(cs, db, appDatabase: appDb);
            Assert.True(await window.ConnectAsync(window, session, null));
            await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;

            window.SqlEditor.Text = $"CREATE USER '{user}'@'localhost' IDENTIFIED BY 'x'";
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            Assert.Equal("1", await ScalarAsync(cs, $"SELECT COUNT(*) FROM mysql.user WHERE User = '{user}'"));
            Assert.Empty(appDb.GetHistory("it"));

            // An ordinary query still goes to the history.
            window.SqlEditor.Text = "SELECT 1";
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            Assert.Single(appDb.GetHistory("it"));
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP USER IF EXISTS '{user}'@'localhost'; DROP DATABASE IF EXISTS `{db}`;");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task F9TwiceWhileTheConfirmationIsOpenRunsOnce()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_lr9_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t (id INT AUTO_INCREMENT PRIMARY KEY);");
        try
        {
            var (window, context, session, _, _) = CreateShell(cs, db);
            session.ConfirmChanges = true;
            var answer = new TaskCompletionSource<(bool, bool)>();
            var questions = 0;
            window.ConfirmOverride = (_, _, _) =>
            {
                questions++;
                return answer.Task;
            };
            Assert.True(await window.ConnectAsync(window, session, null));
            await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            window.SqlEditor.Text = $"INSERT INTO `{db}`.t () VALUES ()";

            // The first F9 waits on "Confirm changes"; the second one finds the tab busy.
            var first = context.Commands.ExecuteAsync("query.run").AsTask();
            Dispatcher.UIThread.RunJobs();
            var second = context.Commands.ExecuteAsync("query.run").AsTask();
            Dispatcher.UIThread.RunJobs();
            answer.SetResult((true, false));
            await first;
            await second;

            Assert.Equal(1, questions);
            Assert.Equal("1", await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.t"));
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    private static async Task<string?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>TCP proxy to the server whose answers can be held back (<see cref="Pause"/>) and let through again.</summary>
    private sealed class GatedProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _host;
        private readonly int _port;
        private readonly Task _accept;
        private readonly Lock _gate = new();
        private TaskCompletionSource _open = NewOpen(completed: true);

        public GatedProxy(string host, int port)
        {
            _host = host;
            _port = port;
            _listener.Start();
            _accept = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Pause()
        {
            lock (_gate)
            {
                if (_open.Task.IsCompleted)
                {
                    _open = NewOpen(completed: false);
                }
            }
        }

        public void Resume()
        {
            lock (_gate)
            {
                _open.TrySetResult();
            }
        }

        private static TaskCompletionSource NewOpen(bool completed)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (completed)
            {
                tcs.SetResult();
            }
            return tcs;
        }

        private Task Opened()
        {
            lock (_gate)
            {
                return _open.Task;
            }
        }

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
                try
                {
                    await server.ConnectAsync(_host, _port, _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException)
                {
                    return;
                }
                var up = PumpAsync(client.GetStream(), server.GetStream(), gated: false);
                var down = PumpAsync(server.GetStream(), client.GetStream(), gated: true);
                await Task.WhenAny(up, down);
            }
        }

        private async Task PumpAsync(NetworkStream from, NetworkStream to, bool gated)
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
                    if (gated)
                    {
                        await Opened().WaitAsync(_stop.Token);
                    }
                    await to.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // Connection closed on one side.
            }
        }

        public async ValueTask DisposeAsync()
        {
            Resume();
            await _stop.CancelAsync();
            _listener.Stop();
            await _accept;
            _stop.Dispose();
        }
    }
}
