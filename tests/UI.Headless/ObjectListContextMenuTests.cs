using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Eunomia.UI.Controls.Common.TreeList;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Commands chosen in the context menu of the Database tab object list, through the real popup. Bug of 18 Nov 2026: the menu
/// takes the keyboard focus when it opens, so "Drop ..." on the selected tables asked to drop the database focused in the tree.
/// </summary>
public sealed class ObjectListContextMenuTests
{
    private static void Click(Window window, TreeListControl list, int row, MouseButton button, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var point = list.TranslatePoint(new Point(30, TreeListControl.HeaderHeight + (row * list.RowHeight) + (list.RowHeight / 2)), window)!.Value;
        window.MouseDown(point, button, modifiers);
        window.MouseUp(point, button, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropFromTheListMenuDropsTheSelectedTablesNotTheDatabase(bool keyboard)
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_olm_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.a (id INT); CREATE TABLE `{db}`.b (id INT); CREATE TABLE `{db}`.c (id INT);");
        try
        {
            var (window, _, session, connector, _) = CreateShell(cs, db);
            var questions = new List<string>();
            window.ConfirmOverride = (_, message, verification) =>
            {
                questions.Add(message);
                return Task.FromResult((true, verification is not null));
            };
            Assert.True(await window.ConnectAsync(window, session, null));
            var root = await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            window.Tree.Tree.FocusedNode = dbNode;
            window.Tabs.SelectedItem = window.Tabs.DatabaseTab;
            var list = window.DatabaseView.List;
            await WaitAsync(() => list.Roots.Count == 3 ? "ok" : null);
            window.CaptureRenderedFrame();

            // a and b selected with the mouse (Ctrl+click), right click on b: the menu opens on the list.
            Click(window, list, 0, MouseButton.Left);
            Click(window, list, 1, MouseButton.Left, RawInputModifiers.Control);
            Assert.Equal(["a", "b"], list.SelectedNodes.Select(n => n[0]));
            Click(window, list, 1, MouseButton.Right);
            var menu = list.ContextMenu!;
            await GridContextMenuTests.ChooseAsync(window, menu, keyboard, "Drop ...");

            await WaitAsync(() => questions.Count > 0 ? "asked" : null);
            Assert.StartsWith($"Drop 2 object(s) in database \"{db}\"?", questions[0], StringComparison.Ordinal);
            await WaitAsync(() => list.Roots.Count == 1 ? "ok" : null, describe: () => string.Join(",", list.Roots.Select(n => n[0])));
            Assert.Equal("c", list.Roots[0][0]);
            Assert.Contains(root.Children!, n => n.Name == db);
            Assert.Single(questions);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
