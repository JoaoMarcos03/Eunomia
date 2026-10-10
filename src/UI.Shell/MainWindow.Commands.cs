using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Eunomia.App.Commands;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Shell.Input;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>Handlers dos comandos do shell e keymap (secção 21.2).</summary>
public sealed partial class MainWindow
{
    /// <summary>Context menu do SQL log (<c>popupSqlLog</c>, anexo C §3.6).</summary>
    private static readonly IReadOnlyList<MenuEntry> SqlLogMenu =
    [
        new CommandEntry("edit.copy"), new CommandEntry("log.copyLineToNewTab"), new CommandEntry("log.clear"),
        new CommandEntry("log.horizontalScrollbar"), new CommandEntry("log.saveToFile"), SeparatorEntry.Instance,
        new CommandEntry("tools.preferencesLogging"),
    ];

    /// <summary>Context menu da barra de tabs (<c>popupMainTabs</c>, anexo C §3.13).</summary>
    private static readonly IReadOnlyList<MenuEntry> TabStripMenu =
    [
        new CommandEntry("query.newTab"), new CommandEntry("query.closeTab"), new CommandEntry("tabs.closeToRight"), new CommandEntry("query.closeAllTabs"),
        SeparatorEntry.Instance,
        new CommandEntry("query.renameTab"), new CommandEntry("query.pinToSession"), new CommandEntry("tabs.closeOnDoubleClick"), new CommandEntry("tabs.closeOnMiddleClick"), new CommandEntry("tabs.multipleLines"),
    ];

    private IInputElement? _lastFocused;

    /// <summary>Elemento com o foco de teclado, ignorando menus e toolbar (que não ficam com o foco).</summary>
    public IInputElement? FocusedElement => FocusManager?.GetFocusedElement() ?? _lastFocused;

    /// <summary>Contextos de foco atuais, do mais interior para o mais exterior.</summary>
    public IReadOnlyList<string> FocusContexts => CommandScope.ContextsOf(FocusedElement);

    private void RegisterCommands()
    {
        var c = _context.Commands;
        var settings = _context.Settings;

        // Edit: encaminhado para o controlo focado (grid, editor, log, caixas de texto).
        c.SetHandler("edit.copy", _ => new ValueTask(EditTarget()!.CopyAsync()), () => EditTarget()?.CanCopy == true);
        c.SetHandler("edit.cut", _ => new ValueTask(EditTarget()!.CutAsync()), () => EditTarget()?.CanCut == true);
        c.SetHandler("edit.paste", _ => new ValueTask(EditTarget()!.PasteAsync()), () => EditTarget()?.CanPaste == true);
        c.SetHandler("edit.selectAll", () => EditTarget()!.SelectAll(), () => EditTarget()?.CanSelectAll == true);
        c.SetHandler("edit.undo", () => EditTarget()!.Undo(), () => EditTarget()?.CanUndo == true);
        c.SetHandler("edit.invertSelection", () => FocusedGrid()!.Execute(GridCommand.InvertSelection), () => FocusedGrid() is not null);

        // Display.
        Toggle("display.objectSizes", () => settings.Display.ObjectSizes, v =>
        {
            settings.Display.ObjectSizes = v;
            _tree.Tree.ShowSizes = v;
        });
        _tree.Tree.ShowSizes = settings.Display.ObjectSizes;
        Toggle("display.onlyFavorites", () => _tree.FavoritesButton.IsChecked == true, v => _tree.FavoritesButton.IsChecked = v);
        Toggle("display.filterPanel", () => settings.Display.FilterPanel, SetFilterPanel);
        Toggle("display.logPanel", () => settings.Display.LogPanel, SetLogPanel);
        Toggle("display.treeFilters", () => settings.Display.TreeFilters, v =>
        {
            settings.Display.TreeFilters = v;
            _tree.FiltersVisible = v;
        });
        c.SetHandler("display.resetPanelDimensions", ResetPanelDimensions);

        // Go to.
        c.SetHandler("goto.tree", () => _tree.Tree.Focus());
        c.SetHandler("goto.tableFilter", () =>
        {
            if (!settings.Display.TreeFilters)
            {
                settings.Display.TreeFilters = true;
                _tree.FiltersVisible = true;
            }
            _tree.FocusTableFilter();
        });
        for (var i = 1; i <= 5; i++)
        {
            var index = i - 1;
            c.SetHandler($"goto.tab{i}", () => _tabs.SelectVisible(index), () => index < _tabs.VisibleTabs.Count);
        }
        c.SetHandler("goto.nextTab", () => _tabs.SelectRelative(1));
        c.SetHandler("goto.previousTab", () => _tabs.SelectRelative(-1));
        c.SetHandler("goto.queryResults", SwitchQueryResults, () => _tabs.ActiveQueryTab is not null);

        // Query tabs.
        c.SetHandler("query.newTab", () => _tabs.AddQueryTab());
        c.SetHandler("query.newTabBackground", () => _tabs.AddQueryTab(select: false));
        c.SetHandler("query.closeTab", _ => new ValueTask(_tabs.RequestCloseAsync(_tabs.TargetQueryTab!)), () => _tabs.TargetQueryTab is { IsClosable: true });
        c.SetHandler("tabs.closeToRight", _ => new ValueTask(_tabs.CloseQueryTabsRightOfAsync(_tabs.TargetQueryTab!)),
            () => _tabs.TargetQueryTab is { } t && _tabs.QueryTabs.Any(q => q.IsClosable && _tabs.Items.IndexOf(q.Item) > _tabs.Items.IndexOf(t.Item)));
        Toggle("tabs.closeOnDoubleClick", () => settings.Tabs.CloseOnDoubleClick, v => settings.Tabs.CloseOnDoubleClick = v);
        Toggle("tabs.closeOnMiddleClick", () => settings.Tabs.CloseOnMiddleClick, v => settings.Tabs.CloseOnMiddleClick = v);
        Toggle("tabs.multipleLines", () => settings.Tabs.MultipleLines, _tabs.SetMultipleLines);
        _tabs.HeaderMenuFactory = () => _ui.BuildTransientContextMenu(TabStripMenu);
        c.SetHandler("query.closeAllTabs", _ => new ValueTask(_tabs.CloseAllQueryTabsAsync()), () => _tabs.QueryTabs.Any(t => t.IsClosable));
        c.SetHandler("query.renameTab", _ => new ValueTask(RenameQueryTabAsync()), () => _tabs.TargetQueryTab is not null);
        c.SetHandler("query.clear", () => SqlEditor.Text = string.Empty, () => _tabs.ActiveQueryTab is not null);
        c.SetHandler("editor.completion", () => SqlEditor.ShowCompletion(), () => _tabs.ActiveQueryTab is not null);
        Toggle("query.wordWrap", () => settings.Query.WordWrap, v =>
        {
            settings.Query.WordWrap = v;
            foreach (var tab in _tabs.QueryTabs.Where(t => t.HasContent))
            {
                QueryEditorOf(tab).Editor.WordWrap = v;
            }
        });
        Toggle("query.stopOnErrors", () => settings.Query.StopOnErrors, v => settings.Query.StopOnErrors = v);
        // Code folding (secção 6): regiões #region/#endregion em todas as query tabs.
        Toggle("query.codeFolding", () => settings.Query.CodeFolding, v =>
        {
            settings.Query.CodeFolding = v;
            foreach (var tab in _tabs.QueryTabs.Where(t => t.HasContent))
            {
                QueryEditorOf(tab).CodeFolding = v;
            }
        });
        c.SetHandler("query.codeFoldingStartRegion", () => SqlEditor.InsertRegionStart(), () => _tabs.ActiveQueryTab is not null && settings.Query.CodeFolding);
        c.SetHandler("query.codeFoldingEndRegion", () => SqlEditor.InsertRegionEnd(), () => _tabs.ActiveQueryTab is not null && settings.Query.CodeFolding);
        c.SetHandler("query.codeFoldingFoldSelection", () => SqlEditor.FoldSelection(),
            () => _tabs.ActiveQueryTab is not null && settings.Query.CodeFolding && SqlEditor.Editor.SelectionLength > 0);
        c.SetHandler("query.batchInOneGo", () => settings.Query.BatchInOneGo = true);
        c.SetChecked("query.batchInOneGo", () => settings.Query.BatchInOneGo);
        c.SetHandler("query.singleQueries", () => settings.Query.BatchInOneGo = false);
        c.SetChecked("query.singleQueries", () => !settings.Query.BatchInOneGo);
        Toggle("grid.blobAsText", () => settings.Data.BlobAsText, v => settings.Data.BlobAsText = v);

        // SQL log.
        c.SetHandler("log.clear", _context.Log.Clear);
        Toggle("log.horizontalScrollbar", () => settings.Log.HorizontalScrollbar, v =>
        {
            settings.Log.HorizontalScrollbar = v;
            _log.HorizontalScrollbar = v;
        });
        c.SetHandler("log.saveToFile", _ => new ValueTask(SaveLogAsync()));
        c.SetHandler("log.copyLineToNewTab", () =>
        {
            var line = _log.LineText(_log.Editor.TextArea.Caret.Line);
            var tab = _tabs.AddQueryTab();
            QueryEditorOf(tab).Text = line;
        }, () => _log.LineCount > 0);

        // Ferramentas, ajuda e ficheiro.
        c.SetHandler("tools.preferences", _ => new ValueTask(ShowPreferencesAsync(null)));
        c.SetHandler("tools.preferencesLogging", _ => new ValueTask(ShowPreferencesAsync(PreferencesWindow.LoggingPage)));
        c.SetHandler("tools.preferencesData", _ => new ValueTask(ShowPreferencesAsync(PreferencesWindow.DataPage)));
        c.SetHandler("help.about", _ => new ValueTask(AboutDialog.ShowAsync(this, ShellContext.Version)));
        c.SetHandler("file.exit", Close);
        c.SetHandler("file.newWindow", StartNewWindow, () => Environment.ProcessPath is not null);
    }

    private void Toggle(string id, Func<bool> get, Action<bool> set)
    {
        _context.Commands.SetHandler(id, () => set(!get()));
        _context.Commands.SetChecked(id, get);
    }

    private IEditTarget? EditTarget() => EditTargets.Find(FocusedElement);

    private DataGridControl? FocusedGrid()
    {
        for (var v = FocusedElement as Avalonia.Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
        {
            if (v is DataGridControl grid)
            {
                return grid;
            }
        }
        return null;
    }

    private IReadOnlyList<MenuEntry> DropdownEntries(string kind) => kind switch
    {
        DynamicMenus.RunOptions => MainMenuLayout.RunDropdown,
        DynamicMenus.Refresh => MainMenuLayout.RefreshDropdown,
        _ => [],
    };

    /// <summary>F6: alterna o foco entre o editor e o grid de resultados da query tab ativa.</summary>
    private void SwitchQueryResults()
    {
        var view = ActiveQueryView!;
        var editor = view.Editor;
        if (editor.Editor.TextArea.IsKeyboardFocusWithin && view.ActiveGrid is { } results)
        {
            results.Focus();
        }
        else
        {
            editor.Editor.TextArea.Focus();
        }
    }

    private async Task RenameQueryTabAsync()
    {
        var tab = _tabs.TargetQueryTab!;
        var name = await InputDialog.ShowAsync(this, "Rename query tab", "New name:", tab.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            tab.SetCaption(name.Trim(), tab.IsModified);
        }
    }

    private async Task SaveLogAsync()
    {
        var file = await PathPicker.SaveFileAsync(this, new FilePickerSaveOptions
        {
            Title = "Save as textfile",
            SuggestedFileName = "sqllog.sql",
            FileTypeChoices = [new FilePickerFileType("SQL files") { Patterns = ["*.sql"] }, new FilePickerFileType("Text files") { Patterns = ["*.txt"] }],
        });
        if (file is null)
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(file, _context.Log.ToText());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MessageDialog.ShowAsync(this, "Save as textfile", ex.Message, MessageDialogButtons.Ok);
        }
    }

    private async Task ShowPreferencesAsync(string? page)
    {
        var dialog = new PreferencesWindow(_context, page);
        await dialog.ShowDialog(this);
        ApplyEditorOptionsToTabs();
        _ui.Refresh();
        _ui.RefreshMenus();
    }

    private void StartNewWindow()
    {
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
    }

    /// <summary>
    /// Keymap (FAM-K01): os atalhos do registo são vistos antes do controlo focado, como as actions do HeidiSQL;
    /// um atalho cujo comando não pode correr não é consumido e segue para o controlo.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        _lastFocused = FocusManager?.GetFocusedElement() ?? _lastFocused;
        if (KeyChordMapper.FromKey(e.Key, e.KeyModifiers) is not { } chord)
        {
            return;
        }
        var command = _context.Commands.ResolveExecutable(chord, FocusContexts);
        if (command is null)
        {
            return;
        }
        e.Handled = true;
        _ui.Execute(command.Id);
    }

    /// <summary>Executa o atalho como se fosse premido (testes e automação).</summary>
    public bool TryExecuteShortcut(string shortcut)
    {
        var command = _context.Commands.ResolveExecutable(KeyChord.Parse(shortcut), FocusContexts);
        if (command is null)
        {
            return false;
        }
        _ui.Execute(command.Id);
        return true;
    }

    internal void LogInfo(string message) => _context.Log.Append(LogCategory.Info, message);

}
