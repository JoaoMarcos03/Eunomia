using System.Diagnostics;
using Avalonia.Threading;
using Eunomia.App.Commands;
using Eunomia.App.Query;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Sql;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.TreeList;
using Eunomia.UI.Features.QueryTab;
using Eunomia.UI.Shell.Commands;
using Eunomia.UI.Shell.Sessions;

namespace Eunomia.UI.Shell;

/// <summary>
/// Painel de helpers das query tabs (anexo C §1.11, §3.9; FAM-Q06, Q09): colunas da tabela selecionada na árvore,
/// funções, keywords, snippets (ficheiros <c>.sql</c> na pasta dos snippets) e o histórico da sessão alvo. Duplo clique
/// ou "Insert at cursor" insere; os snippets carregam-se, renomeiam-se e apagam-se; o histórico limpa-se por sessão ou
/// por entrada; "Generate …" escreve SQL para as colunas escolhidas.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Substitui o pedido do nome de um snippet (testes): título e nome atual → nome novo.</summary>
    internal Func<string, string, string?>? SnippetNameOverride { get; set; }

    private string SnippetsDirectory => QueryHelpers.SnippetsDirectory(_context.Settings.Query.SnippetsDirectory);

    /// <summary>Linguagem da sessão da query tab ativa (nomes inseridos pelos helpers).</summary>
    private ISqlLanguage HelperLanguage =>
        ActiveQueryView is { } view && TargetOf(StateOf(view)).Session is { } session ? session.Language : SqlLanguages.Default;

    private void InitHelpers()
    {
        var c = _context.Commands;
        c.SetHandler("helpers.insert", () => InsertHelpers(), () => FocusedHelperItems().Any(i => QueryHelpers.InsertText(HelperLanguage, i, _ => string.Empty) is not null));
        c.SetHandler("helpers.loadSnippet", _ => new ValueTask(LoadSnippetAsync()), () => FocusedSnippet() is not null);
        c.SetHandler("helpers.renameSnippet", _ => new ValueTask(RenameSnippetAsync()), () => FocusedSnippet() is not null);
        c.SetHandler("helpers.deleteSnippet", _ => new ValueTask(DeleteSnippetAsync()), () => FocusedSnippet() is not null);
        c.SetHandler("helpers.exploreFolder", ExploreSnippets, () => FocusedSnippet() is not null);
        c.SetHandler("helpers.clearHistory", _ => new ValueTask(ClearHistoryAsync()),
            () => FocusedHelperItems().FirstOrDefault() is { Kind: HelperKind.HistoryDay or HelperKind.HistoryItem } && _context.AppDatabase is not null);
        c.SetCaption("helpers.clearHistory", () => FocusedHelperItems().FirstOrDefault() is { Kind: HelperKind.HistoryItem } ? "Delete this query from history" : null);
        foreach (var kind in new[] { "Select", "Insert", "Update", "Delete" })
        {
            c.SetHandler("helpers.generate" + kind, () => GenerateFromColumns(kind), () => HelperColumns().Table is not null);
        }
        c.SetHandler("query.saveSnippet", _ => new ValueTask(SaveSnippetAsync(selection: false)), () => ActiveQueryView is { Editor.Text.Length: > 0 });
        c.SetHandler("query.saveSelectionSnippet", _ => new ValueTask(SaveSnippetAsync(selection: true)), () => ActiveQueryView is { Editor.Editor.SelectionLength: > 0 });
        _tabs.SelectionChanged += (_, _) =>
        {
            if (ActiveQueryView is { } view)
            {
                RefreshHelpers(view);
            }
        };
    }

    private QueryHelpersView CreateHelpersView(QueryTabView view)
    {
        var helpers = new QueryHelpersView();
        CommandScope.SetContext(helpers, CommandContexts.Helpers);
        // Depois de a tab ter conteúdo: montar o menu avalia os comandos, que olham para a query tab ativa.
        Dispatcher.UIThread.Post(() => helpers.Tree.ContextMenu = _ui.BuildContextMenu(MainMenuLayout.HelpersMenu));
        helpers.Tree.NodeActivated += (_, node) =>
        {
            if (node.Tag is HelperItem { Kind: not (HelperKind.Root or HelperKind.HistoryDay) })
            {
                InsertHelpers();
            }
        };
        return helpers;
    }

    private QueryHelpersView? ActiveHelpers =>
        _tabs.ActiveQueryTab is { HasContent: true } tab && tab.Content is QueryTabView view ? view.Helpers as QueryHelpersView : null;

    /// <summary>Itens escolhidos no painel da query tab ativa (o focado primeiro).</summary>
    private IReadOnlyList<HelperItem> FocusedHelperItems()
    {
        if (ActiveHelpers is not { } helpers)
        {
            return [];
        }
        var focused = helpers.Tree.FocusedNode?.Tag as HelperItem;
        var selected = helpers.Tree.SelectedNodes.Select(n => n.Tag).OfType<HelperItem>().ToList();
        if (focused is not null && !selected.Contains(focused))
        {
            selected.Insert(0, focused);
        }
        return selected;
    }

    private HelperItem? FocusedSnippet() => FocusedHelperItems().FirstOrDefault() is { Kind: HelperKind.Snippet } snippet ? snippet : null;

    /// <summary>Tabela ou view selecionada na árvore, na sessão onde a query tab corre.</summary>
    private DbObjectRef? HelperTable(OpenSession? session) =>
        session is not null && _tree.Tree.FocusedNode?.Tag is TreeObjectTag { Object: { Kind: DbObjectKind.Table or DbObjectKind.View } obj } tag && tag.Session == session.Id
            ? obj
            : null;

    /// <summary>
    /// Volta a montar o painel da tab (colunas da cache; se ainda não estão, carrega e volta a montar).
    /// <paramref name="onlyIfTableChanged"/>: nada a fazer se a tabela selecionada é a mesma (navegar na árvore).
    /// </summary>
    private void RefreshHelpers(QueryTabView view, bool onlyIfTableChanged = false)
    {
        if (view.Helpers is not QueryHelpersView helpers)
        {
            return;
        }
        var state = StateOf(view);
        var session = TargetOf(state).Session;
        var table = HelperTable(session);
        var columns = table is not null ? session!.Metadata.CachedTable(table)?.Columns : null;
        if (onlyIfTableChanged && state.HelpersShown == (table, columns is not null))
        {
            return;
        }
        state.HelpersShown = (table, columns is not null);
        if (table is not null && columns is null)
        {
            _ = LoadHelperColumnsAsync(view, session!, table);
        }
        IReadOnlyList<string> snippets;
        try
        {
            snippets = Directory.Exists(SnippetsDirectory) ? Directory.GetFiles(SnippetsDirectory, "*.sql") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            snippets = [];
        }
        var history = session is not null && _context.AppDatabase is { } db ? db.GetHistory(session.Name) : [];
        var language = session?.Language ?? SqlLanguages.Default;
        helpers.SetRoots([.. QueryHelpers.Build(language, table, columns, snippets, history, DateTime.Now).Select(ToNode)]);
    }

    private async Task LoadHelperColumnsAsync(QueryTabView view, OpenSession session, DbObjectRef table)
    {
        try
        {
            await session.Metadata.GetTableAsync(table, CancellationToken.None);
            Dispatcher.UIThread.Post(() => RefreshHelpers(view));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Sem colunas no painel; o erro já foi para o SQL log pela sessão.
        }
    }

    private static TreeListNode ToNode(HelperItem item)
    {
        var node = new TreeListNode(item.Text, item.Attribute)
        {
            Tag = item,
            IsGroup = item.Kind is HelperKind.Root or HelperKind.HistoryDay,
            Icon = item.Kind switch
            {
                HelperKind.Column => "table",
                HelperKind.Function => "function",
                HelperKind.Snippet => "save-snippet",
                HelperKind.HistoryItem => "run",
                _ => null,
            },
        };
        foreach (var child in item.Children)
        {
            node.Add(ToNode(child));
        }
        return node;
    }

    /// <summary>Insert at cursor: o texto dos itens escolhidos (separados por vírgulas) no editor da tab.</summary>
    private void InsertHelpers()
    {
        if (ActiveQueryView is not { } view)
        {
            return;
        }
        var language = HelperLanguage;
        var texts = FocusedHelperItems().Select(i => QueryHelpers.InsertText(language, i, ReadSnippet)).OfType<string>().ToList();
        if (texts.Count > 0)
        {
            view.Editor.InsertAtCaret(string.Join(", ", texts));
        }
    }

    private string ReadSnippet(string path)
    {
        try
        {
            return App.Query.SqlFile.Decode(File.ReadAllBytes(path), App.Query.SqlFile.AutoDetect).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
            return string.Empty;
        }
    }

    /// <summary>Colunas para os "Generate …": as escolhidas, ou todas se o foco está na raiz "Columns".</summary>
    private (DbObjectRef? Table, IReadOnlyList<string> Columns) HelperColumns()
    {
        var items = FocusedHelperItems();
        var columns = items.Where(i => i.Kind == HelperKind.Column).ToList();
        if (columns.Count == 0 && items.FirstOrDefault() is { Kind: HelperKind.Root } root && root.Children.All(c => c.Kind == HelperKind.Column))
        {
            columns = [.. root.Children];
        }
        return columns is [{ Payload: DbObjectRef table }, ..] ? (table, columns.Select(c => c.Text).ToList()) : (null, []);
    }

    private void GenerateFromColumns(string kind)
    {
        if (HelperColumns() is { Table: { } table, Columns: { Count: > 0 } columns } && ActiveQueryView is { } view)
        {
            view.Editor.InsertAtCaret(QueryHelpers.Generate(HelperLanguage, kind, table, columns));
        }
    }

    private async Task<string?> AskSnippetNameAsync(string title, string current) =>
        SnippetNameOverride is { } fake ? fake(title, current) : await InputDialog.ShowAsync(this, title, "Snippet name:", current);

    /// <summary>Save as snippet / Save selection as snippet: um <c>.sql</c> (UTF-8) na pasta dos snippets.</summary>
    private async Task SaveSnippetAsync(bool selection)
    {
        var view = ActiveQueryView!;
        var text = selection ? view.Editor.Editor.SelectedText : view.Editor.Text;
        var name = await AskSnippetNameAsync(selection ? "Save selection as snippet" : "Save as snippet", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var path = Path.Combine(SnippetsDirectory, QueryHelpers.SnippetFileName(name));
        if (File.Exists(path) && await AskAsync($"Overwrite existing snippet {Path.GetFileNameWithoutExtension(path)}?", MessageDialogButtons.YesNo) != MessageDialogResult.Yes)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(SnippetsDirectory);
            await File.WriteAllBytesAsync(path, App.Export.ExportFile.Encode(text, "UTF-8"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(ex);
            return;
        }
        RefreshHelpers(view);
    }

    private async Task LoadSnippetAsync()
    {
        if (FocusedSnippet() is { Payload: string path })
        {
            await LoadFileAsync(path, App.Query.SqlFile.AutoDetect);
        }
    }

    private async Task RenameSnippetAsync()
    {
        if (FocusedSnippet() is not { Payload: string path } snippet || ActiveQueryView is not { } view)
        {
            return;
        }
        var name = await AskSnippetNameAsync("Rename snippet", snippet.Text);
        if (string.IsNullOrWhiteSpace(name) || name == snippet.Text)
        {
            return;
        }
        try
        {
            File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, QueryHelpers.SnippetFileName(name)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(ex);
        }
        RefreshHelpers(view);
    }

    private async Task DeleteSnippetAsync()
    {
        if (FocusedSnippet() is not { Payload: string path } snippet || ActiveQueryView is not { } view)
        {
            return;
        }
        if (await AskAsync($"Delete snippet file \"{snippet.Text}\"?", MessageDialogButtons.YesNo) != MessageDialogResult.Yes)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(ex);
        }
        RefreshHelpers(view);
    }

    /// <summary>
    /// Explore folder: a pasta do snippet selecionado no gestor de ficheiros do sistema (Explorador e Finder com o
    /// ficheiro selecionado; no Linux, a pasta pelo <c>xdg-open</c>).
    /// </summary>
    private void ExploreSnippets()
    {
        if (FocusedSnippet() is not { Payload: string path })
        {
            return;
        }
        try
        {
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", ["-R", path])
                : new ProcessStartInfo("xdg-open", [Path.GetDirectoryName(path) ?? path]);
            using var _ = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _context.Log.Append(Presentation.Logging.LogCategory.Error, "Could not open the folder: " + ex.Message);
        }
    }

    /// <summary>Clear query history (toda a da sessão) ou Delete this query from history (uma entrada).</summary>
    private async Task ClearHistoryAsync()
    {
        if (_context.AppDatabase is not { } db || ActiveQueryView is not { } view || FocusedHelperItems().FirstOrDefault() is not { } item)
        {
            return;
        }
        if (item is { Kind: HelperKind.HistoryItem, Payload: App.Settings.QueryHistoryEntry entry })
        {
            db.DeleteHistory(entry.Id);
        }
        else if (TargetOf(StateOf(view)).Session is { } session
                 && await AskAsync($"Clear query history of session \"{session.Name}\"?", MessageDialogButtons.YesNo) == MessageDialogResult.Yes)
        {
            db.ClearHistory(session.Name);
        }
        RefreshHelpers(view);
    }

    /// <summary>Snippets ▸ do dropdown do Load SQL file.</summary>
    private Avalonia.Controls.MenuItem SnippetsMenu()
    {
        var menu = new Avalonia.Controls.MenuItem { Header = "Snippets" };
        try
        {
            if (Directory.Exists(SnippetsDirectory))
            {
                foreach (var path in Directory.GetFiles(SnippetsDirectory, "*.sql").Order(StringComparer.CurrentCultureIgnoreCase))
                {
                    var item = new Avalonia.Controls.MenuItem { Header = Path.GetFileNameWithoutExtension(path) };
                    item.Click += (_, _) => _ = LoadFileAsync(path, App.Query.SqlFile.AutoDetect);
                    menu.Items.Add(item);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
        }
        menu.IsEnabled = menu.Items.Count > 0;
        return menu;
    }
}
