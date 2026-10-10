using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Eunomia.App.Query;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Features.QueryTab;
using Eunomia.UI.Shell;
using Eunomia.UI.Shell.Views;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Load, Save, Save as, recent files, external change and closing changed tabs (appendix C §3.2, §4.3).</summary>
public sealed class SqlFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eunomia-sf-" + Guid.NewGuid().ToString("N")[..8]);

    public SqlFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task LoadSaveRecentAndExternalChange()
    {
        var window = new MainWindow(TestShell.CreateContext());
        window.Show();
        var commands = window.Context.Commands;
        var questions = new List<string>();
        var answer = MessageDialogResult.Yes;
        window.FileQuestionOverride = q =>
        {
            questions.Add(q);
            return answer;
        };
        window.LoadEncodingOverride = () => SqlFile.AutoDetect;
        var path = Path.Combine(_dir, "a.sql");
        await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("SELECT 'ação';")]);
        window.OpenSqlFilesOverride = () => [path];
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;

        // Load: the empty tab becomes the file, without *, and goes to the recent files.
        Assert.True(await commands.ExecuteAsync("query.load"));
        var tab = window.Tabs.QueryTabs[0];
        var view = Assert.IsType<QueryTabView>(tab.Content);
        Assert.Equal("SELECT 'ação';", view.Editor.Text);
        Assert.Equal(("a.sql", false), (tab.Name, tab.IsModified));
        Assert.Equal(path, window.Context.Settings.Query.RecentFiles[0]);
        var count = window.Tabs.QueryTabs.Count;
        Assert.True(await commands.ExecuteAsync("query.load"));
        Assert.Equal(count, window.Tabs.QueryTabs.Count);

        // Editing marks *; Save writes with the same encoding (BOM).
        view.Editor.Editor.Document.Insert(view.Editor.Text.Length, "\nSELECT 2;");
        Assert.True(tab.IsModified);
        Assert.True(await commands.ExecuteAsync("query.save"));
        Assert.False(tab.IsModified);
        Assert.Equal([0xEF, 0xBB, 0xBF], (await File.ReadAllBytesAsync(path))[..3]);
        Assert.EndsWith("SELECT 2;", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

        // External change: "Reload file?" and the editor reloads.
        await Task.Delay(50);
        await File.WriteAllTextAsync(path, "SELECT 3;");
        await WaitAsync(() => view.Editor.Text == "SELECT 3;" ? "ok" : null);
        Assert.StartsWith("File was modified from outside:", questions[^1], StringComparison.Ordinal);

        // Save as in a new tab: UTF-8 without a BOM and the caption with the name.
        var second = window.Tabs.AddQueryTab();
        var secondView = Assert.IsType<QueryTabView>(second.Content);
        secondView.Editor.Text = "SELECT 'new';";
        var other = Path.Combine(_dir, "b.sql");
        window.SaveSqlFileOverride = suggested =>
        {
            Assert.Equal("Query #2.sql", suggested);
            return other;
        };
        Assert.True(await commands.ExecuteAsync("query.saveAs"));
        Assert.Equal("b.sql", second.Name);
        Assert.Equal(Encoding.UTF8.GetBytes("SELECT 'new';"), await File.ReadAllBytesAsync(other));

        // Closing a changed tab asks; Cancel keeps it, No closes it.
        secondView.Editor.Text = "SELECT 'mudou';";
        answer = MessageDialogResult.Cancel;
        Assert.False(await window.Tabs.RequestCloseAsync(second));
        Assert.Equal($"Save changes to file {other} ?", questions[^1]);
        answer = MessageDialogResult.No;
        Assert.True(await window.Tabs.RequestCloseAsync(second));
        Assert.DoesNotContain(second, window.Tabs.QueryTabs);

        // Recent files in the drop-down; Remove absent files takes out the ones that no longer exist.
        File.Delete(other);
        var items = window.LoadSqlItems().OfType<MenuItem>().ToList();
        // The paths are escaped ("_" → "__"), which the menu draws as "_" (on macOS the temporary folder has "_").
        Assert.Equal(["Snippets", $"1 {LiteralText.Escaped(other)}", $"2 {LiteralText.Escaped(path)}", "Remove absent files", "Clear file list"], items.Select(i => (string)i.Header!));
        items.Single(i => (string)i.Header! == "Remove absent files").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal([path], window.Context.Settings.Query.RecentFiles);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task LargeFileAsksFirst()
    {
        var window = new MainWindow(TestShell.CreateContext());
        window.Show();
        var path = Path.Combine(_dir, "big.sql");
        await File.WriteAllTextAsync(path, new string('x', (int)SqlFile.LargeFileBytes + 10));
        IReadOnlyList<(string Path, long Bytes)>? asked = null;
        window.LargeFilesOverride = files =>
        {
            asked = files;
            return LargeFilesChoice.Cancel;
        };
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
        Assert.False(await window.LoadFileAsync(path, SqlFile.AutoDetect));
        Assert.Equal([(path, SqlFile.LargeFileBytes + 10)], asked);
        Assert.Equal(string.Empty, window.SqlEditor.Text);

        // "Load file(s) into the editor" opens it.
        window.LargeFilesOverride = _ => LargeFilesChoice.Load;
        Assert.True(await window.LoadFileAsync(path, SqlFile.AutoDetect));
        Assert.Equal((int)SqlFile.LargeFileBytes + 10, window.SqlEditor.Text.Length);
        window.Close();
    }
}
