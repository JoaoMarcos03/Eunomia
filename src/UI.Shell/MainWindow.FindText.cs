using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Shell.Sessions;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Find text on server (Ctrl+Shift+F; Search menu and popupDB): the dialog on the focused database; each count runs on a jobs
/// connection (the tree and the query tabs stay free); "See results" opens a query tab with the SELECTs and runs it.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Open dialog (tests).</summary>
    internal FindTextDialog? FindTextWindow { get; private set; }

    private void InitFindText()
    {
        _context.Commands.SetHandler("tools.findTextOnServer", _ => new ValueTask(FindTextOnServerAsync()), () => ActiveSession is { Host.ServerServices: not null });
    }

    private async Task FindTextOnServerAsync()
    {
        if (ActiveSession is not { Host.ServerServices: { } services } open)
        {
            return;
        }
        var databases = await open.Metadata.GetDatabasesAsync(CancellationToken.None);
        var focus = _tree.Tree.FocusedNode?.Tag as TreeObjectTag;
        var dialog = new FindTextDialog(new FindTextServices(
            services.Dialect,
            databases,
            focus?.Database ?? open.Host.CurrentDatabase,
            focus?.Object is { Kind: DbObjectKind.Table } table ? table.Name : null,
            async (db, ct) =>
            {
                var schema = await open.Host.RunMetadataAsync((h, c) => h.Metadata.GetSchemaAsync(db, db, c), ct);
                var cached = open.Metadata.CachedObjects(db)?.Objects;
                return schema.Select(t => (t, cached?.FirstOrDefault(o => o.Ref.Name == t.Ref.Name)?.RowsEstimate)).ToList();
            },
            async (sql, ct) => Convert.ToInt64(await open.Host.RunJobAsync((h, c) => h.ExecuteScalarAsync(sql, c), ct) ?? 0L, CultureInfo.InvariantCulture),
            sql =>
            {
                var tab = _tabs.AddQueryTab();
                QueryEditorOf(tab).Text = sql;
                _ = RunQueryAsync(RunScope.All);
            }));
        FindTextWindow = dialog;
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            FindTextWindow = null;
        }
    }
}
