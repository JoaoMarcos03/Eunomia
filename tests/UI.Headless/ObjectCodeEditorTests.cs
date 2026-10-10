using Avalonia.Headless.XUnit;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Tree;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Views and routines edited through the CREATE in the Table tab (phase 13), against the test server.</summary>
public sealed class ObjectCodeEditorTests
{
    private static async Task<string?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task CreateViewAlterProcedureAndRestoreOriginalOnError()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_oc_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t (a INT); CREATE PROCEDURE `{db}`.p() SELECT 1 AS one;");
        try
        {
            var (window, context, session, connector, _) = CreateShell(cs, db);
            var errors = new List<string>();
            window.EditErrorOverride = errors.Add;
            var questions = new List<string>();
            window.SaveTableQuestionOverride = q =>
            {
                questions.Add(q);
                return Task.FromResult(MessageDialogResult.No);
            };
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);

            // Create new ▸ View: the template, edited and saved, creates the view and focuses it in the tree.
            window.Tree.Tree.FocusedNode = dbNode;
            Assert.True(await context.Commands.ExecuteAsync("object.createView"));
            var created = await WaitAsync(() => window.CodeEditor is { IsNew: true } e ? e : null);
            Assert.StartsWith("CREATE VIEW `view_name` AS", created.Code, StringComparison.Ordinal);
            created.Editor.Text = "CREATE VIEW `v_a` AS SELECT a FROM t";
            Assert.True(await window.SaveObjectCodeAsync(created));
            Assert.Empty(errors);
            Assert.Equal("1", await ScalarAsync(cs, $"SELECT COUNT(*) FROM information_schema.VIEWS WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = 'v_a'"));
            await WaitAsync(() => window.Tree.Tree.FocusedNode is { Name: "v_a", Kind: TreeNodeKind.View } ? "ok" : null);

            // Existing procedure: opens with the server's code; changing and saving replaces the body.
            var procedure = await WaitAsync(() => TreeModel.ObjectsOf(window.Tree.Tree.FocusedNode!.Parent!.Kind == TreeNodeKind.Group ? window.Tree.Tree.FocusedNode.Parent.Parent! : window.Tree.Tree.FocusedNode.Parent!).FirstOrDefault(n => n.Name == "p"));
            window.Tree.Tree.FocusedNode = procedure;
            var editor = await WaitAsync(() => window.CodeEditor is { IsNew: false, Kind: Eunomia.Drivers.Abstractions.DbObjectKind.Procedure } e && e.Code.Contains("SELECT 1", StringComparison.Ordinal) ? e : null);
            Assert.False(editor.IsModified);
            editor.Editor.Text = editor.Code.Replace("SELECT 1 AS one", "SELECT 2 AS two", StringComparison.Ordinal);
            Assert.True(await window.SaveObjectCodeAsync(editor));
            Assert.Contains("SELECT 2 AS two", await ScalarAsync(cs, $"SELECT ROUTINE_DEFINITION FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{db}' AND ROUTINE_NAME = 'p'"), StringComparison.Ordinal);

            // Code with an error: the error shows and the old procedure exists again.
            editor = await WaitAsync(() => window.CodeEditor is { Kind: Eunomia.Drivers.Abstractions.DbObjectKind.Procedure } e && e.Code.Contains("SELECT 2", StringComparison.Ordinal) ? e : null);
            editor.Editor.Text = "CREATE PROCEDURE `p`() SELEKT nonsense";
            Assert.False(await window.SaveObjectCodeAsync(editor));
            Assert.NotEmpty(errors);
            Assert.Contains("SELECT 2 AS two", await ScalarAsync(cs, $"SELECT ROUTINE_DEFINITION FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{db}' AND ROUTINE_NAME = 'p'"), StringComparison.Ordinal);

            // Leaving with the code changed asks (No discards).
            window.Tree.Tree.FocusedNode = dbNode;
            await WaitAsync(() => questions.Count == 1 ? "ok" : null);
            Assert.Equal("Save modified procedure \"p\"?", questions[0]);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
