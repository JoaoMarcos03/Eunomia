using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Eunomia.App.Query;
using Eunomia.Engine.Transfer;
using Eunomia.UI.Shell.Views;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Run SQL file pela janela principal (Fase 8; anexo B §4.1–4.2): ficheiro grande no Load, progresso e Resume.</summary>
public sealed class RunSqlFileTests
{
    private static async Task<string?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task FicheiroGrandeCorreDiretoEParadoNumErroRetoma()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_rf_");
        var dir = Path.Combine(Path.GetTempPath(), "eunomia-it", "runfile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await ExecAsync(cs, $"CREATE DATABASE `{db}`");
        try
        {
            var journal = new TransferJournal(Path.Combine(dir, "jobs"));
            var (window, context, session, connector, log) = CreateShell(cs, db, journal: journal);
            Assert.True(await window.ConnectAsync(window, session, null));
            // A base focada na árvore passa a ser a atual da sessão (o ficheiro corre nela).
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            window.Tree.Tree.FocusedNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            var open = connector.Find(session.Id)!;
            await WaitAsync(() => open.Host.CurrentDatabase == db ? "ok" : null);

            // Mais de 5 MiB: o Load pergunta e "Run file(s) directly" corre-o sem o abrir.
            var big = new StringBuilder("CREATE TABLE t (id INT PRIMARY KEY, txt VARCHAR(200));\r\n");
            var row = 0;
            var statements = 0;
            // INSERTs de 100 linhas: o servidor faz um commit (fsync) por instrução, e 58.000 levavam minutos.
            while (big.Length < SqlFile.LargeFileBytes + 100_000)
            {
                statements++;
                big.Append("INSERT INTO t VALUES ");
                for (var r = 0; r < 100; r++)
                {
                    row++;
                    big.Append(CultureInfo.InvariantCulture, $"{(r > 0 ? ", " : string.Empty)}({row}, '{new string('x', 80)} ação')");
                }
                big.Append(";\r\n");
            }
            var bigPath = Path.Combine(dir, "big.sql");
            await File.WriteAllTextAsync(bigPath, big.ToString(), new UTF8Encoding(false));
            IReadOnlyList<(string Path, long Bytes)>? asked = null;
            window.LargeFilesOverride = files =>
            {
                asked = files;
                return LargeFilesChoice.Run;
            };
            window.OpenSqlFilesOverride = () => [bigPath];
            window.LoadEncodingOverride = () => SqlFile.AutoDetect;
            Assert.True(await context.Commands.ExecuteAsync("query.load"));
            await window.RunningFiles;

            Assert.Single(asked!);
            Assert.Equal(row.ToString(CultureInfo.InvariantCulture), await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.t"));
            Assert.Equal(string.Empty, window.SqlEditor.Text);
            Assert.Contains(log.Snapshot(), l => l.Text.Contains($"File {bigPath} executed, with {statements + 1} queries and {row} affected rows", StringComparison.Ordinal));
            var run = Assert.IsType<SqlFileRunWindow>(window.RunWindow);
            Assert.Equal("1 file(s) processed", run.Line1.Split(',')[0]);
            Assert.False(run.ResumeButton.IsVisible);
            run.Close();

            // "Run SQL file..." com Stop on errors: pára na instrução que falha; Resume continua dela.
            var broken = Path.Combine(dir, "broken.sql");
            await File.WriteAllTextAsync(broken, "INSERT INTO u VALUES (1);\r\nINSERT INTO u VALUES (2);\r\nINSERT INTO falta VALUES (1);\r\nINSERT INTO u VALUES (3);\r\n");
            await ExecAsync(cs, $"CREATE TABLE `{db}`.u (a INT)");
            window.OpenSqlFilesOverride = () => [broken];
            var running = context.Commands.ExecuteAsync("query.runFile").AsTask();
            var stopped = await WaitAsync(() => window.RunWindow is { Running: false } w && w.ResumeButton.IsVisible ? w : null);
            Assert.StartsWith("Stopped at query #3:", stopped.Line1, StringComparison.Ordinal);
            Assert.Contains(log.Snapshot(), l => l.Text.Contains("Error in query #3 (line 3):", StringComparison.Ordinal));
            Assert.Single(journal.List());
            Assert.Equal("2", await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.u"));

            await ExecAsync(cs, $"CREATE TABLE `{db}`.falta (a INT)");
            stopped.ResumeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => window.RunWindow is { Running: false } w && w.Line1.StartsWith("1 file(s) processed", StringComparison.Ordinal) ? "ok" : null);
            await running;
            Assert.Equal("3", await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.u"));
            Assert.Equal("1", await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.falta"));
            Assert.Empty(journal.List());
            window.RunWindow?.Close();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
            Directory.Delete(dir, recursive: true);
        }
    }
}
