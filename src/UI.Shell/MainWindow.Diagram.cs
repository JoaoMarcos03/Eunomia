using Avalonia.Controls;
using Eunomia.App.Diagram;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.SchemaVisualizer;

namespace Eunomia.UI.Shell;

/// <summary>
/// Schema Visualizer in the shell (phase 9; section 17.3): "Show in schema visualizer" in the tree opens (or activates) the
/// session's "Diagram: database" tab, with the focused table highlighted. Selecting a table in the tree highlights and centres
/// the node without stealing the focus; selecting a node selects the table in the tree ("Sync with tree"). Double click opens
/// the Data tab, Alt+Enter the table editor; F5 reads the schema again keeping the positions; the layout is saved in <c>app.db</c>.
/// </summary>
public sealed partial class MainWindow
{
    private sealed record DiagramEntry(OpenSession Session, string Database, TabItem Tab, SchemaVisualizerView View);

    private readonly List<DiagramEntry> _diagrams = [];
    private bool _syncingFromDiagram;

    internal IReadOnlyList<SchemaVisualizerView> DiagramViews => [.. _diagrams.Select(d => d.View)];

    private void InitDiagram()
    {
        _context.Commands.SetHandler("diagram.show", _ => new ValueTask(ShowDiagramFromTreeAsync()), () => TreeTarget() is { Node: { } node } && node.Database is not null);
        _tabs.DiagramTabClosed += (_, tab) =>
        {
            if (_diagrams.FirstOrDefault(d => ReferenceEquals(d.Tab, tab)) is { } entry)
            {
                SaveDiagram(entry);
                _diagrams.Remove(entry);
            }
        };
        Closing += (_, _) =>
        {
            foreach (var entry in _diagrams)
            {
                SaveDiagram(entry);
            }
        };
    }

    private Task ShowDiagramFromTreeAsync()
    {
        if (TreeTarget() is not { } target || target.Node.Database is not { } dbNode)
        {
            return Task.CompletedTask;
        }
        var table = target.Node.Kind == TreeNodeKind.Table ? target.Node.Name : null;
        return ShowDiagramAsync(target.Session, dbNode.Name, table);
    }

    /// <summary>Opens the database's diagram (or activates the one already open) with <paramref name="table"/> highlighted.</summary>
    internal async Task ShowDiagramAsync(OpenSession session, string database, string? table)
    {
        if (_diagrams.FirstOrDefault(d => ReferenceEquals(d.Session, session) && d.Database == database) is { } open)
        {
            _tabs.SelectedItem = open.Tab;
            if (table is not null)
            {
                open.View.Highlight(table);
            }
            return;
        }
        var view = new SchemaVisualizerView(database);
        view.Canvas.SessionColor = session.Color ?? 0;
        var tab = _tabs.AddDiagramTab("Diagram: " + database, view, session.Color ?? 0);
        var entry = new DiagramEntry(session, database, tab, view);
        _diagrams.Add(entry);
        view.OpenTableRequested += async (_, name) =>
        {
            if (await FocusFromDiagramAsync(entry, name))
            {
                ShowDataTab();
            }
        };
        view.EditTableRequested += async (_, name) =>
        {
            if (await FocusFromDiagramAsync(entry, name))
            {
                _tabs.SelectedItem = _tabs.TableTab;
            }
        };
        view.SelectTopRequested += async (_, name) =>
        {
            if (await FocusFromDiagramAsync(entry, name))
            {
                await _context.Commands.ExecuteAsync("object.selectTopRows");
            }
        };
        view.TableSelected += async (_, name) => await FocusFromDiagramAsync(entry, name);
        view.RefreshRequested += async (_, _) => await LoadDiagramAsync(entry, null);
        view.StateChanged += (_, _) => SaveDiagram(entry);
        await LoadDiagramAsync(entry, table);
    }

    private async Task LoadDiagramAsync(DiagramEntry entry, string? table)
    {
        try
        {
            var tables = await entry.Session.Metadata.GetSchemaAsync(entry.Database, CancellationToken.None);
            var diagram = SchemaDiagram.From(entry.Database, tables);
            var first = entry.View.Diagram is null;
            var saved = first && _context.AppDatabase is { } db ? DiagramLayoutState.Load(db, entry.Session.Name, entry.Database) : null;
            await entry.View.ShowAsync(diagram, saved, table);
        }
        catch (Exception e) when (entry.Session.Host.StatementError(e) is not null || e is SessionConnectionLostException or SessionOperationException)
        {
            _context.Log.Append(LogCategory.Error, $"Schema visualizer ({entry.Database}): {e.Message}");
        }
    }

    /// <summary>The active tab's diagram, if it is one.</summary>
    private DiagramEntry? ActiveDiagram() => _diagrams.FirstOrDefault(d => ReferenceEquals(d.Tab, _tabs.SelectedItem));

    private Task RefreshDiagramAsync(DiagramEntry diagram) => LoadDiagramAsync(diagram, null);

    private void SaveDiagram(DiagramEntry entry)
    {
        if (_context.AppDatabase is { } db && entry.View.Diagram is not null)
        {
            entry.View.CurrentState().Save(db, entry.Session.Name, entry.Database);
        }
    }

    /// <summary>Visualizer → tree: focuses the table without the tree centring the diagram again.</summary>
    private async Task<bool> FocusFromDiagramAsync(DiagramEntry entry, string table)
    {
        _syncingFromDiagram = true;
        try
        {
            return await FocusTreeObjectAsync(entry.Session.Id, new DbObjectRef(entry.Database, entry.Database, table, DbObjectKind.Table));
        }
        finally
        {
            _syncingFromDiagram = false;
        }
    }

    /// <summary>Tree → visualizer: the focused table is highlighted and centred in the database's open diagrams.</summary>
    private void SyncDiagramsWithTree(TreeNode node)
    {
        if (_syncingFromDiagram || node.Kind != TreeNodeKind.Table || node.Database is not { } dbNode || node.Session?.Tag is not Guid id)
        {
            return;
        }
        foreach (var entry in _diagrams.Where(d => d.Session.Id == id && d.Database == dbNode.Name))
        {
            entry.View.Highlight(node.Name);
        }
    }
}
