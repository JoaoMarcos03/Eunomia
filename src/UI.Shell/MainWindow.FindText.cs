using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Shell.Sessions;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Find text on server (Ctrl+Shift+F; Search menu e popupDB): o diálogo na base focada; cada contagem corre numa ligação
/// de jobs (a árvore e as query tabs continuam livres); "See results" abre uma query tab com os SELECT e corre-a.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Diálogo aberto (testes).</summary>
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
