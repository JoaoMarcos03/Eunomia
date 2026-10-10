using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Features.Transfer;
using Eunomia.UI.Shell.Sessions;

namespace Eunomia.UI.Shell;

/// <summary>
/// Maintenance (Tools ▸ Maintenance e popupDB; anexo B §2.2): o diálogo na base focada, com os objetos escolhidos na
/// árvore ou na lista do Database tab; cada operação corre numa ligação de jobs. OPTIMIZE e REPAIR perguntam antes, com a
/// sessão e o servidor, como as outras ações que mexem em tabelas.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Diálogo aberto (testes).</summary>
    internal MaintenanceDialog? MaintenanceWindow { get; private set; }

    private void InitMaintenance()
    {
        _context.Commands.SetHandler("tools.maintenance", _ => new ValueTask(MaintenanceAsync()), () => ActiveSession is { Host.ServerServices: not null });
    }

    private async Task MaintenanceAsync()
    {
        if (ActiveSession is not { Host.ServerServices: { } services } open)
        {
            return;
        }
        var databases = await open.Metadata.GetDatabasesAsync(CancellationToken.None);
        var target = ObjectsTarget();
        var database = target?.Database ?? (_tree.Tree.FocusedNode?.Tag as TreeObjectTag)?.Database ?? open.Host.CurrentDatabase;
        var dialog = new MaintenanceDialog(new MaintenanceServices(
            databases,
            database,
            target is { } t && t.Session == open ? [.. t.Objects.Select(o => o.Name)] : [],
            async (db, ct) => (await open.Metadata.GetObjectsAsync(db, ct)).Objects,
            services.Dialect.MaintenanceOptionsOf,
            async (owner, operation, count, options) =>
            {
                var question = TableMaintenance.Question(operation, count, options) + SessionLine(open);
                if (ConfirmOverride is { } confirm)
                {
                    return (await confirm("Confirm", question, null)).Ok;
                }
                return await MessageDialog.ShowAsync(owner, "Confirm", question, MessageDialogButtons.OkCancel) == MessageDialogResult.Ok;
            },
            (operation, table, options, ct) => TableMaintenance.RunAsync(open.Host, operation, table, options, ct)));
        MaintenanceWindow = dialog;
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            MaintenanceWindow = null;
        }
    }
}
