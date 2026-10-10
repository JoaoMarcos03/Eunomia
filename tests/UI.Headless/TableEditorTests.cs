using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Controls.Tree;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Table editor against the server (phase 7; appendix C §5; FAM-E01–E03).</summary>
public sealed class TableEditorTests
{
    private static async Task<string?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task EditATableAndCreateAnother()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_te_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.people (id INT NOT NULL AUTO_INCREMENT, name VARCHAR(20) NULL, PRIMARY KEY (id)) ENGINE=InnoDB COMMENT='people';");
        try
        {
            var (window, context, session, _, _) = CreateShell(cs, db);
            var errors = new List<string>();
            window.EditErrorOverride = errors.Add;
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);

            // The table opens in the editor with its columns, indexes and comment.
            window.Tree.Tree.FocusedNode = Child(dbNode, "people");
            var editor = await WaitAsync(() => window.TableEditor);
            var design = editor.Design;
            Assert.Equal(["id", "name"], design.Columns.Select(c => c.Name));
            Assert.Equal(DefaultKind.AutoIncrement, design.Columns[0].DefaultKind);
            Assert.Equal("people", design.Comment);
            Assert.Equal("Indexes (1)", editor.IndexesTab.Header);

            // Changing: renames and resizes a column, adds another one and an index; the ALTER shows live.
            design.Columns[1].Name = "full_name";
            design.Columns[1].LengthSet = "60";
            design.Columns.Add(new ColumnDesign { Name = "age", DataType = "SMALLINT", Unsigned = true, AllowNull = true, DefaultKind = DefaultKind.Null });
            design.Indexes.Add(new IndexDesign { Name = "ix_age", Kind = IndexKind.Key, Columns = "age" });
            editor.Refresh();
            Assert.Contains("CHANGE COLUMN `name` `full_name` varchar(60)", editor.AlterCodeText, StringComparison.Ordinal);
            Assert.Contains("ADD INDEX `ix_age` (`age`)", editor.AlterCodeText, StringComparison.Ordinal);
            Assert.True(await window.SaveTableAsync(editor));
            Assert.Empty(errors);
            Assert.Equal("full_name,varchar(60);age,smallint(5) unsigned", await ScalarAsync(cs,
                $"SELECT GROUP_CONCAT(CONCAT(COLUMN_NAME, ',', COLUMN_TYPE) ORDER BY ORDINAL_POSITION SEPARATOR ';') FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = 'people' AND COLUMN_NAME <> 'id'"));
            var reopened = await WaitAsync(() => window.TableEditor is { Design.Columns.Count: 3 } e ? e : null);
            Assert.Equal(string.Empty, reopened.AlterCodeText);

            // Create new ▸ Table on the focused database: without a name it does not save; with a name, columns and a key it creates the table.
            window.Tree.Tree.FocusedNode = dbNode;
            Assert.True(await context.Commands.ExecuteAsync("object.createTable"));
            var created = await WaitAsync(() => window.TableEditor is { Design.Name: "" } e ? e : null);
            created.Design.Columns.Add(new ColumnDesign { Name = "code", DataType = "CHAR", LengthSet = "3", AllowNull = false });
            Assert.False(await window.SaveTableAsync(created));
            Assert.Equal("Please enter a table name.", errors[^1]);
            created.Design.Name = "codes";
            created.Design.Indexes.Add(new IndexDesign { Name = "PRIMARY", Kind = IndexKind.Primary, Columns = "code" });
            created.Refresh();
            Assert.StartsWith($"CREATE TABLE `{db}`.`codes`", created.CreateCodeText, StringComparison.Ordinal);
            Assert.True(await window.SaveTableAsync(created));
            Assert.Equal("1", await ScalarAsync(cs, $"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = 'codes'"));
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task LeavingTheEditorWithChangesAsks()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_tl_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.people (id INT PRIMARY KEY, name VARCHAR(20)); CREATE TABLE `{db}`.pets (id INT);");
        try
        {
            var (window, _, session, _, _) = CreateShell(cs, db);
            var questions = new List<string>();
            var answer = Eunomia.UI.Controls.Common.Dialogs.MessageDialogResult.Cancel;
            window.SaveTableQuestionOverride = q =>
            {
                questions.Add(q);
                return Task.FromResult(answer);
            };
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);
            var people = Child(dbNode, "people");
            var pets = Child(dbNode, "pets");

            async Task<Eunomia.UI.Features.TableEditor.TableEditorView> EditPeopleAsync(string column)
            {
                window.Tree.Tree.FocusedNode = people;
                var editor = await WaitAsync(() => window.TableEditor is { Design.Name: "people" } e ? e : null);
                await Task.Delay(300);
                Dispatcher.UIThread.RunJobs();
                Assert.False(editor.IsModified, "Editor marked as changed on opening.");
                editor.Design.Columns[1].Name = column;
                editor.Refresh();
                return editor;
            }

            // Cancel: the tree goes back to the table and the editor keeps the changes.
            var editor = await EditPeopleAsync("full_name");
            window.Tree.Tree.FocusedNode = pets;
            await WaitAsync(() => questions.Count == 1 && ReferenceEquals(window.Tree.Tree.FocusedNode, people) ? "ok" : null);
            Assert.Equal("Save modified table \"people\"?", questions[0]);
            Assert.Same(editor, window.TableEditor);
            Assert.True(editor.IsModified);

            // No: discards and moves on to the other table; the server stays the same.
            answer = Eunomia.UI.Controls.Common.Dialogs.MessageDialogResult.No;
            window.Tree.Tree.FocusedNode = pets;
            await WaitAsync(() => window.TableEditor is { Design.Name: "pets" } ? "ok" : null);
            Assert.Equal(2, questions.Count);
            Assert.Equal("name", await ScalarAsync(cs, $"SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = 'people' AND ORDINAL_POSITION = 2"));

            // Yes: saves and moves on.
            await EditPeopleAsync("full_name");
            answer = Eunomia.UI.Controls.Common.Dialogs.MessageDialogResult.Yes;
            window.Tree.Tree.FocusedNode = pets;
            await WaitAsync(() => window.TableEditor is { Design.Name: "pets" } ? "ok" : null);
            Assert.Equal("full_name", await ScalarAsync(cs, $"SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = 'people' AND ORDINAL_POSITION = 2"));

            // Closing the window with changes: Cancel does not close; No closes.
            await EditPeopleAsync("given_name");
            answer = Eunomia.UI.Controls.Common.Dialogs.MessageDialogResult.Cancel;
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            answer = Eunomia.UI.Controls.Common.Dialogs.MessageDialogResult.No;
            window.Close();
            await WaitAsync(() => window.IsVisible ? null : "closed");
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
