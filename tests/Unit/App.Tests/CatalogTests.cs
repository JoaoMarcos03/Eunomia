using Eunomia.App.Commands;

namespace Eunomia.App.Tests;

/// <summary>Command inventory (section 21.2, FAM-L02, FAM-K01): complete against HeidiSQL and without conflicts.</summary>
public sealed class CatalogTests
{
    private static readonly CommandRegistry Registry = HeidiSqlCatalog.CreateRegistry();

    private static IReadOnlyList<string> HeidiSqlActions() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "heidisql-actions.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    [Fact]
    public void All163HeidiSqlActionsHaveACommand()
    {
        var actions = HeidiSqlActions();
        Assert.Equal(163, actions.Count);

        var missing = actions.Where(a => Registry.FindByHeidiSqlAction(a) is null).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void EachActionMapsToASingleCommand()
    {
        var duplicated = Registry.All.Where(c => c.HeidiSqlAction is not null)
            .GroupBy(c => c.HeidiSqlAction, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicated);
    }

    [Fact]
    public void Has16CopyAsInHeidiSqlOrder()
    {
        Assert.Equal(
            ["Excel CSV", "Delimited text", "HTML table", "XML", "SQL INSERTs", "SQL INSERT IGNOREs", "SQL REPLACEs", "SQL DELETEs/INSERTs",
             "SQL UPDATEs", "LaTeX", "Textile", "Jira Textile", "PHP Array", "Markdown Here", "JSON", "JSON Lines"],
            HeidiSqlCatalog.CopyAsFormats.Select(f => Registry.Get(f.Id).Caption));
    }

    [Fact]
    public void DefaultShortcutsHaveNoConflicts()
    {
        Assert.Empty(Registry.FindConflicts());
    }

    [Theory]
    [InlineData("actExecuteQuery", "F9")]
    [InlineData("actExecuteSelection", "Ctrl+F9")]
    [InlineData("actExecuteCurrentQuery", "Ctrl+Shift+F9")]
    [InlineData("actRefresh", "F5")]
    [InlineData("actFullRefresh", "Shift+F5")]
    [InlineData("actNewQueryTab", "Ctrl+T")]
    [InlineData("actCloseQueryTab", "Ctrl+F4")]
    [InlineData("actClearQueryEditor", "Ctrl+W")]
    [InlineData("actLoadSQL", "Ctrl+O")]
    [InlineData("actSaveSQL", "Ctrl+S")]
    [InlineData("actSaveSQLAs", "F12")]
    [InlineData("actQueryFind", "Ctrl+F")]
    [InlineData("actQueryReplace", "Ctrl+R")]
    [InlineData("actQueryFindAgain", "F3")]
    [InlineData("actFindTextOnServer", "Ctrl+Shift+F")]
    [InlineData("actSynEditCompletionPropose", "Ctrl+Space")]
    [InlineData("actReformatSQL", "Ctrl+F8")]
    [InlineData("actGotoFilter", "Ctrl+E")]
    [InlineData("actGotoDbTree", "Ctrl+D")]
    [InlineData("actGoToQueryResults", "F6")]
    [InlineData("actGoToDataMultiFilter", "F7")]
    [InlineData("actGotoTab1", "Ctrl+1")]
    [InlineData("actGotoTab5", "Ctrl+5")]
    [InlineData("actNextTab", "Ctrl+Tab")]
    [InlineData("actPreviousResult", "Alt+Left")]
    [InlineData("actNextResult", "Alt+Right")]
    [InlineData("actDataInsert", "Ins")]
    [InlineData("actDataDuplicateRowWithoutKeys", "Ctrl+Ins")]
    [InlineData("actDataDelete", "Ctrl+Del")]
    [InlineData("actDataPostChanges", "Ctrl+Enter")]
    [InlineData("actDataCancelChanges", "Esc")]
    [InlineData("actDataSetNull", "Ctrl+Shift+N")]
    [InlineData("actGridEditFunction", "Ctrl+F2")]
    [InlineData("actDataResetSorting", "Alt+S")]
    [InlineData("actDataShowNext", "Ctrl+Alt+PgDn")]
    [InlineData("actDataShowAll", "Ctrl+Alt+End")]
    [InlineData("actFilterPanel", "Ctrl+Alt+F")]
    [InlineData("actQuickFilterFocused1", "Ctrl+Shift+1")]
    [InlineData("actQuickFilterClipboard2", "Ctrl+Shift+6")]
    [InlineData("actEmptyTables", "Shift+Del")]
    [InlineData("actSQLhelp", "F1")]
    [InlineData("actPrintList", "Ctrl+P")]
    [InlineData("actNewWindow", "Ctrl+N")]
    public void MostUsedShortcutsOfFamK01(string action, string shortcut)
    {
        var command = Registry.FindByHeidiSqlAction(action);

        Assert.NotNull(command);
        Assert.Equal(KeyChord.Parse(shortcut), command.DefaultShortcut);
    }

    [Fact]
    public void MenuItemShortcuts()
    {
        Assert.Equal(KeyChord.Parse("Alt+Enter"), Registry.Get("object.edit").DefaultShortcut);
        Assert.Equal(KeyChord.Parse("Del"), Registry.Get("host.killProcess").DefaultShortcut);
        Assert.Equal(KeyChord.Parse("Alt+F4"), Registry.Get("file.exit").DefaultShortcut);
    }

    [Fact]
    public void CategoriesAreHeidiSqls()
    {
        Assert.All(Registry.All, c => Assert.Contains(c.Category, HeidiSqlCatalog.Categories));
    }

    [Fact]
    public void MenusReferToExistingCommands()
    {
        var unknown = MainMenuLayout.ReferencedCommandIds().Where(id => !Registry.TryGet(id, out _)).ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void MainMenusInHeidiSqlOrder()
    {
        Assert.Equal(["_File", "_Edit", "_Display", "_Search", "_Query", "_Tools", "_Go to", "_Help"], MainMenuLayout.Menus.Select(m => m.Caption));
    }

    [Fact]
    public void FileMenuInAppendixCOrder()
    {
        var file = MainMenuLayout.Menus[0].Items.Select(Describe);

        Assert.Equal(
            ["Session manager", "Connect to", "New window", "New query tab", "Close query tab", "Close all query tabs", "-",
             "Load SQL file...", "Run SQL file...", "Save", "Save as snippet ...", "-",
             "Export settings file ...", "Import settings file ...", "-", "Exit"],
            file);
    }

    [Fact]
    public void ToolbarOnlyWithEverydayUse()
    {
        var buttons = MainMenuLayout.Toolbar.Where(t => !t.IsSeparator).Select(t => t.CommandId).ToList();

        Assert.Equal(
            ["session.manager", "session.disconnect", "view.refresh", "grid.first", "grid.last", "grid.insertRow", "grid.deleteRows",
             "grid.post", "grid.cancelEditing", "query.newTab", "query.run", "operation.cancel", "query.stopOnErrors", "query.load",
             "query.save", "search.find", "search.replace", "import.csv", "export.databaseAsSql", "export.gridRows", "tools.preferences"],
            buttons);
        Assert.All(buttons, id => Assert.Null(Registry.Get(id!).AvailableIn));
    }

    [Fact]
    public void ItemsOutsideTheMvpGetAVersion()
    {
        foreach (var id in new[] { "tools.userManager", "tools.flushHosts", "tools.bulkTableEditor" })
        {
            Assert.Equal(HeidiSqlCatalog.AfterMvp, Registry.Get(id).AvailableIn);
            Assert.Equal("Available in version 1.1", Registry.StateOf(id).Tooltip);
        }
    }

    private static string Describe(MenuEntry entry) => entry switch
    {
        CommandEntry c => Registry.Get(c.CommandId).Caption,
        SeparatorEntry => "-",
        SubmenuEntry s => s.Caption,
        DynamicEntry d => d.Caption,
        _ => "?",
    };
}
