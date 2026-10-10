using Avalonia.Headless.XUnit;
using Eunomia.App.Query;
using Eunomia.App.Settings;
using Eunomia.Infra.Settings;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Features.QueryTab;
using Eunomia.UI.Shell;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Helpers panel: sections, filter, Insert at cursor, snippets and history (appendix C §1.11, §3.9; FAM-Q06, Q09).</summary>
public sealed class QueryHelpersPanelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eunomia-qh-" + Guid.NewGuid().ToString("N")[..8]);

    public QueryHelpersPanelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task SeccoesFiltroInsertSnippetsEHistorico()
    {
        var settings = new AppSettings();
        settings.Query.SnippetsDirectory = Path.Combine(_dir, "snippets");
        using var appDb = new SqliteAppDatabase(Path.Combine(_dir, "app.db"));
        var window = new MainWindow(new ShellContext(settings, new TestShell.MemorySettingsStore(settings), appDb));
        window.Show();
        var commands = window.Context.Commands;
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
        var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[0].Content);
        var helpers = Assert.IsType<QueryHelpersView>(view.Helpers);
        Assert.Equal(["Columns", "SQL functions", "SQL keywords", "Snippets", "Query history"], helpers.Tree.Roots.Select(r => r[0]));

        // Filter: only the functions that contain the text, with the root open.
        helpers.Filter.Text = "concat_w";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var functions = helpers.Tree.Roots[QueryHelpers.FunctionsRoot];
        Assert.True(functions.IsExpanded);
        var match = Assert.Single(helpers.Tree.VisibleNodes, n => n.Depth == 1);
        Assert.Equal("CONCAT_WS", match[0]);

        // Insert at cursor (the same as double click).
        view.Editor.Text = "SELECT ";
        view.Editor.Editor.CaretOffset = 7;
        helpers.Tree.FocusedNode = match;
        Assert.True(await commands.ExecuteAsync("helpers.insert"));
        Assert.Equal("SELECT CONCAT_WS", view.Editor.Text);
        helpers.Filter.Text = string.Empty;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Save as snippet: a file in the folder, shows in the panel; Insert at cursor inserts the content; Delete asks.
        window.SnippetNameOverride = (_, _) => "meu/snippet";
        Assert.True(await commands.ExecuteAsync("query.saveSnippet"));
        var file = Path.Combine(settings.Query.SnippetsDirectory, "meu_snippet.sql");
        Assert.Equal("SELECT CONCAT_WS", await File.ReadAllTextAsync(file));
        var snippet = Assert.Single(helpers.Tree.Roots[QueryHelpers.SnippetsRoot].Children);
        Assert.Equal("meu_snippet", snippet[0]);
        view.Editor.Text = string.Empty;
        helpers.Tree.FocusedNode = snippet;
        Assert.True(await commands.ExecuteAsync("helpers.insert"));
        Assert.Equal("SELECT CONCAT_WS", view.Editor.Text);
        window.FileQuestionOverride = _ => MessageDialogResult.Yes;
        Assert.True(await commands.ExecuteAsync("helpers.deleteSnippet"));
        Assert.False(File.Exists(file));
        Assert.Empty(helpers.Tree.Roots[QueryHelpers.SnippetsRoot].Children);

        // Snippets ▸ and recent files in the Load drop-down.
        Assert.IsType<Avalonia.Controls.MenuItem>(window.LoadSqlItems()[0]);
        window.Close();
    }
}
