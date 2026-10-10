using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Eunomia.UI.Controls.Common.Dialogs;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Open transaction in the UI (section 14.5; phase 14): status bar and the question on disconnecting and closing.</summary>
public sealed class TransactionUiTests
{
    private static async Task<long> CountAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task StatusBarEPerguntaAoDesligarComAlteracoesPorConfirmar()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_txui_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t (id INT PRIMARY KEY) ENGINE=InnoDB;");
        try
        {
            var (window, context, session, _, _) = CreateShell(cs, db);
            var questions = new List<string>();
            var answer = MessageDialogResult.Cancel;
            window.TransactionQuestionOverride = q =>
            {
                questions.Add(q);
                return Task.FromResult(answer);
            };
            Assert.True(await window.ConnectAsync(window, session, null));
            await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;

            window.SqlEditor.Text = $"START TRANSACTION;\nINSERT INTO `{db}`.t VALUES (1);";
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            await WaitAsync(() => context.Status.Transaction == "Uncommitted changes" ? "ok" : null);
            var status = window.GetLogicalDescendants().OfType<Eunomia.UI.Shell.Views.StatusBarView>().Single();
            Assert.Equal("Uncommitted changes", status.PanelText(2));

            // Cancel: the session stays connected and the transaction open.
            var open = Assert.Single(context.Sessions!.Connector.Open);
            await window.DisconnectAsync(open);
            Assert.Single(questions);
            Assert.Single(context.Sessions.Connector.Open);
            Assert.Equal(0, await CountAsync(cs, $"SELECT COUNT(*) FROM `{db}`.t"));

            // Yes: COMMIT (disconnecting the last session would open the Session Manager, so the session is closed separately).
            answer = MessageDialogResult.Yes;
            Assert.True(await window.ResolveOpenTransactionAsync(open));
            Assert.Equal(2, questions.Count);
            Assert.Equal(1, await CountAsync(cs, $"SELECT COUNT(*) FROM `{db}`.t"));
            await WaitAsync(() => context.Status.Transaction.Length == 0 ? "ok" : null);
            await context.Sessions.Connector.DisconnectAsync(open);
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
