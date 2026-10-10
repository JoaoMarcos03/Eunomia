namespace Eunomia.App.Commands;

/// <summary>Entry of a menu generated from the command registry (section 21.2).</summary>
public abstract record MenuEntry;

/// <summary>Item bound to a registry command (caption, shortcut, icon and state come from the command).</summary>
public sealed record CommandEntry(string CommandId) : MenuEntry;

public sealed record SeparatorEntry : MenuEntry
{
    public static readonly SeparatorEntry Instance = new();
}

public sealed record SubmenuEntry(string Caption, IReadOnlyList<MenuEntry> Items, string? Icon = null) : MenuEntry;

/// <summary>Submenu built at runtime by the UI ("Connect to ▸", "Editor commands ▸").</summary>
public sealed record DynamicEntry(string Kind, string Caption) : MenuEntry;

public sealed record MenuDefinition(string Caption, IReadOnlyList<MenuEntry> Items);

/// <summary>Main toolbar button; <see cref="Dropdown"/> is the button's menu (split button).</summary>
public sealed record ToolbarEntry(string? CommandId, string? Dropdown = null)
{
    public bool IsSeparator => CommandId is null;

    public static readonly ToolbarEntry Separator = new((string?)null);
}

/// <summary>Kinds of <see cref="DynamicEntry"/> and of toolbar drop-downs.</summary>
public static class DynamicMenus
{
    public const string ConnectTo = "connectTo";
    public const string EditorCommands = "editorCommands";
    public const string Sessions = "sessions";
    public const string Refresh = "refresh";
    public const string RunOptions = "run";
    public const string LoadSql = "loadSql";
}

/// <summary>
/// Geography of HeidiSQL's main menu (appendix C §1.4; FAM-L02), with the same items in the same order, and the
/// reduced toolbar of phase 13. Items without a handler (outside the MVP) are hidden by the UI.
/// </summary>
public static class MainMenuLayout
{
    private static CommandEntry I(string id) => new(id);

    private static readonly SeparatorEntry S = SeparatorEntry.Instance;

    public static IReadOnlyList<MenuDefinition> Menus { get; } =
    [
        new("_File",
        [
            I("session.manager"), new DynamicEntry(DynamicMenus.ConnectTo, "Connect to"), I("file.newWindow"), I("query.newTab"),
            I("query.closeTab"), I("query.closeAllTabs"), S,
            I("query.load"), I("query.runFile"), I("query.save"), I("query.saveSnippet"), S,
            I("settings.export"), I("settings.import"), S,
            I("file.exit"),
        ]),
        new("_Edit",
        [
            I("edit.undo"), I("edit.copy"), I("edit.copyFormatted"), I("edit.copyTabsToSpaces"), I("edit.copyColumnNames"),
            I("edit.copyGridNodes"), I("edit.paste"), I("edit.cut"), I("editor.moveLineDown"), I("editor.moveLineUp"), S,
            I("edit.selectAll"), I("edit.invertSelection"),
        ]),
        new("_Display",
        [
            I("display.objectSizes"), I("display.onlyFavorites"), I("display.filterPanel"), I("display.logPanel"),
            I("display.treeFilters"), I("display.columnsInTree"), S,
            I("display.resetPanelDimensions"),
        ]),
        new("_Search",
        [
            I("search.find"), I("search.findAgain"), I("search.replace"), I("tools.findTextOnServer"),
        ]),
        new("_Query",
        [
            I("query.newTab"), I("query.closeTab"), I("query.renameTab"), I("query.pinToSession"), I("query.clear"), S,
            I("query.run"), I("query.runSelection"), I("query.runCurrent"), I("query.batchInOneGo"), I("query.singleQueries"), S,
            I("query.nextResult"), I("query.previousResult"), S,
            I("query.setDelimiter"), I("query.reformat"), I("query.wordWrap"), I("query.toggleComment"),
            new DynamicEntry(DynamicMenus.EditorCommands, "Editor commands"),
            new SubmenuEntry("Code folding",
            [
                I("query.codeFolding"), I("query.codeFoldingStartRegion"), I("query.codeFoldingEndRegion"), I("query.codeFoldingFoldSelection"),
            ]),
            S,
            I("query.explainCurrent"),
        ]),
        new("_Tools",
        [
            new SubmenuEntry("Flush",
            [
                I("tools.flushHosts"), I("tools.flushLogs"), I("tools.flushPrivileges"), I("tools.flushTables"),
                I("tools.flushTablesWithReadLock"), I("tools.flushStatus"),
            ]),
            S,
            I("tools.userManager"), I("tools.maintenance"), I("tools.bulkTableEditor"), I("tools.generateData"),
            I("tools.launchCommandLine"), I("tools.sequalSuggest"), S,
            I("export.databaseAsSql"), I("export.gridRows"), S,
            I("import.csv"), I("import.filesIntoBlob"), S,
            I("tools.preferences"), I("tools.commandPalette"),
        ]),
        new("_Go to",
        [
            I("goto.previousTab"), I("goto.nextTab"), I("query.previousResult"), I("query.nextResult"), S,
            I("goto.tableFilter"), I("goto.tree"), I("goto.queryResults"), I("goto.multiColumnFilter"),
            I("goto.tab1"), I("goto.tab2"), I("goto.tab3"), I("goto.tab4"), I("goto.tab5"),
        ]),
        new("_Help",
        [
            I("help.sql"), I("help.general"), S,
            I("help.checkUpdates"), I("help.downloadPage"), I("help.forum"), I("help.changelog"), I("help.about"),
        ]),
    ];

    /// <summary>
    /// Main toolbar (phase 13): the HeidiSQL buttons used every day, in HeidiSQL's order (sessions, refresh, data
    /// navigation and editing, execution, files, search, import/export), plus "New query tab" and Preferences.
    /// Cancel sits next to Run. The other commands stay in the menus, with the same shortcuts.
    /// </summary>
    public static IReadOnlyList<ToolbarEntry> Toolbar { get; } =
    [
        new("session.manager", DynamicMenus.Sessions), new("session.disconnect"), ToolbarEntry.Separator,
        new("view.refresh", DynamicMenus.Refresh), ToolbarEntry.Separator,
        new("grid.first"), new("grid.last"), new("grid.insertRow"), new("grid.deleteRows"), new("grid.post"), new("grid.cancelEditing"),
        ToolbarEntry.Separator,
        new("query.newTab"), new("query.run", DynamicMenus.RunOptions), new("operation.cancel"), new("query.stopOnErrors"), ToolbarEntry.Separator,
        new("query.load", DynamicMenus.LoadSql), new("query.save"), new("search.find"), new("search.replace"), ToolbarEntry.Separator,
        new("import.csv"), new("export.databaseAsSql"), new("export.gridRows"), ToolbarEntry.Separator,
        new("tools.preferences"),
    ];

    /// <summary>Context menu of the tree and of the Database tab list (<c>popupDB</c>, appendix C §3.3).</summary>
    public static IReadOnlyList<MenuEntry> DatabaseMenu { get; } =
    [
        I("object.edit"), I("object.drop"), I("object.attachDatabase"), I("object.detachDatabase"), I("object.emptyTables"),
        I("object.selectTopRows"), I("object.runRoutines"), I("diagram.show"),
        new SubmenuEntry("Create new",
        [
            I("object.createDatabase"), I("object.createTable"), I("object.copyTable"), I("object.createView"), I("object.createProcedure"),
            I("object.createFunction"), I("object.createTrigger"), I("object.createEvent"),
        ], "add"),
        I("object.clearDataTabFilter"), S,
        I("export.databaseAsSql"), I("tools.maintenance"), I("tools.findTextOnServer"), I("tools.bulkTableEditor"), I("tools.generateData"), S,
        I("tree.expandAll"), I("tree.collapseAll"),
        new SubmenuEntry("Tree style options",
        [
            I("tree.groupObjects"), I("display.objectSizes"), I("tree.autoExpand"), I("tree.doubleClickInsertsNodeText"),
            I("display.onlyFavorites"), I("display.columnsInTree"),
        ]),
        S, I("list.print"), I("view.refresh"), I("session.properties"), I("session.disconnect"),
    ];

    /// <summary>Quick Filter "More values" submenu (the column's 50 most frequent values).</summary>
    public const string QuickFilterValues = "quickFilterValues";

    private static IReadOnlyList<MenuEntry> InsertValueMenu() =>
    [
        I("grid.setNull"), I("grid.sqlFunction"), I("insertValue.default"), I("grid.editWithoutLookup"), S,
        I("insertValue.datetime"), I("insertValue.date"), I("insertValue.time"), I("insertValue.year"), I("insertValue.unixTimestamp"), S,
        I("insertValue.utcDatetime"), I("insertValue.utcDate"), I("insertValue.utcTime"), I("insertValue.utcUnixTimestamp"), S,
        I("insertValue.guid"), I("insertValue.guidNoBraces"), I("insertValue.guidLower"), I("insertValue.guidLowerNoBraces"),
    ];

    /// <summary>Quick Filter (appendix D B10): focused values, More values, a typed value, clipboard and Remove filter.</summary>
    private static IReadOnlyList<MenuEntry> QuickFilterMenu() =>
    [
        I("quickFilter.focused1"), I("quickFilter.focused2"), I("quickFilter.focused3"), I("quickFilter.focused4"), I("quickFilter.focused5"),
        I("quickFilter.focused6"), I("quickFilter.focused7"), new DynamicEntry(QuickFilterValues, "More values"), S,
        I("quickFilter.prompt1"), I("quickFilter.prompt2"), I("quickFilter.prompt3"), I("quickFilter.prompt4"), I("quickFilter.prompt5"),
        I("quickFilter.null"), I("quickFilter.notNull"), S,
        I("quickFilter.clipboard1"), I("quickFilter.clipboard2"), I("quickFilter.clipboard3"), I("quickFilter.clipboard4"),
        I("quickFilter.clipboard5"), I("quickFilter.clipboard6"), S,
        I("filter.remove"),
    ];

    /// <summary>Context menu of the helpers panel (<c>popupQueryHelpers</c>, appendix C §3.9).</summary>
    public static IReadOnlyList<MenuEntry> HelpersMenu { get; } =
    [
        I("helpers.insert"), S,
        I("helpers.loadSnippet"), I("helpers.renameSnippet"), I("helpers.deleteSnippet"), I("helpers.exploreFolder"), S,
        I("help.sql"), I("helpers.clearHistory"), S,
        I("helpers.generateSelect"), I("helpers.generateInsert"), I("helpers.generateUpdate"), I("helpers.generateDelete"),
    ];

    /// <summary>Context menu of the Data grid (<c>popupDataGrid</c>, appendix C §3.5).</summary>
    public static IReadOnlyList<MenuEntry> DataGridMenu { get; } =
    [
        I("edit.copy"),
        new SubmenuEntry("Copy as", HeidiSqlCatalog.CopyAsFormats.Select(f => (MenuEntry)I(f.Id)).ToList(), "copy"),
        I("edit.paste"),
        new SubmenuEntry("Insert value", InsertValueMenu()),
        I("import.filesIntoBlob"), I("grid.saveBlob"),
        new SubmenuEntry("Grid view options", [I("grid.unixTimestamp"), I("grid.imagePreview"), I("grid.blobAsText"), I("tools.preferencesData")]),
        I("grid.openUrl"), I("grid.followForeignKey"), S,
        I("grid.insertRow"), I("grid.duplicateRow"), I("grid.duplicateRowWithKeys"), I("grid.post"), I("grid.cancelEditing"), I("grid.deleteRows"), S,
        I("grid.resetSorting"),
        new SubmenuEntry("Quick Filter", QuickFilterMenu(), "filter"),
        I("search.find"), S,
        I("export.gridRows"), I("help.sql"), I("view.refresh"),
    ];

    /// <summary>Context menu of the Host tab lists (<c>popupHost</c>, appendix C §3.4).</summary>
    public static IReadOnlyList<MenuEntry> HostMenu { get; } =
    [
        I("edit.copy"), S, I("host.fetchDatabaseItems"), I("host.killProcess"), I("host.editVariable"), I("host.explainProcess"), S,
        I("list.print"), I("view.refresh"),
    ];

    /// <summary>Run button drop-down (<c>popupExecuteQuery</c>, appendix C §3.14).</summary>
    public static IReadOnlyList<MenuEntry> RunDropdown { get; } =
    [
        I("query.run"), I("query.runSelection"), I("query.runCurrent"), S, I("query.batchInOneGo"), I("query.singleQueries"),
    ];

    /// <summary>Refresh button drop-down (<c>popupRefresh</c>, appendix C §3.12).</summary>
    public static IReadOnlyList<MenuEntry> RefreshDropdown { get; } =
    [
        I("view.fullRefresh"), I("view.autoRefresh"), I("view.autoRefreshInterval"),
    ];

    /// <summary>Every id referenced by the menu, toolbar and drop-downs (to validate against the registry).</summary>
    public static IEnumerable<string> ReferencedCommandIds()
    {
        IEnumerable<string> Walk(IEnumerable<MenuEntry> entries)
        {
            foreach (var e in entries)
            {
                switch (e)
                {
                    case CommandEntry c:
                        yield return c.CommandId;
                        break;
                    case SubmenuEntry s:
                        foreach (var id in Walk(s.Items))
                        {
                            yield return id;
                        }
                        break;
                }
            }
        }

        return Menus.SelectMany(m => Walk(m.Items))
            .Concat(Toolbar.Where(t => !t.IsSeparator).Select(t => t.CommandId!))
            .Concat(Walk(RunDropdown))
            .Concat(Walk(RefreshDropdown))
            .Concat(Walk(DatabaseMenu))
            .Concat(Walk(HostMenu))
            .Concat(Walk(DataGridMenu))
            .Concat(Walk(HelpersMenu));
    }
}
