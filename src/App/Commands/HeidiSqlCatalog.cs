namespace Eunomia.App.Commands;

/// <summary>
/// Inventário de comandos (secção 21.2; anexo C §2): as 163 actions do <c>ActionList1</c> do HeidiSQL, as 16
/// "Copy as" criadas em runtime e os atalhos definidos em itens de menu. A ordem e as categorias são as do
/// HeidiSQL (as categorias dão os grupos do Preferences › Shortcuts). Itens fora do MVP (secção 27.2) têm
/// <c>AvailableIn</c>.
/// </summary>
public static class HeidiSqlCatalog
{
    /// <summary>Versão em que entram os itens da secção 27.2 (Fases 10 e 11).</summary>
    public const string AfterMvp = "1.1";

    /// <summary>Categorias pela ordem do <c>ActionList1</c> (raízes do Preferences › Shortcuts).</summary>
    public static readonly IReadOnlyList<string> Categories = ["File", "Various", "Tools", "Export/Import", "Database", "SQL", "Data"];

    /// <summary>Os 16 formatos de "Copy as", pela ordem e com os nomes do HeidiSQL (FAM-C02, secção 4.6).</summary>
    /// <summary>Itens do submenu "Insert value" depois de NULL, SQL function e Edit without lookup (anexo C §3.5).</summary>
    public static readonly IReadOnlyList<(string Id, string Caption)> InsertValues =
    [
        ("insertValue.default", "default"),
        ("insertValue.datetime", "datetime"),
        ("insertValue.date", "date"),
        ("insertValue.time", "time"),
        ("insertValue.year", "year"),
        ("insertValue.unixTimestamp", "unix timestamp"),
        ("insertValue.utcDatetime", "utc datetime"),
        ("insertValue.utcDate", "utc date"),
        ("insertValue.utcTime", "utc time"),
        ("insertValue.utcUnixTimestamp", "utc unix timestamp"),
        ("insertValue.guid", "GUID"),
        ("insertValue.guidNoBraces", "GUID without braces"),
        ("insertValue.guidLower", "GUID lowercase"),
        ("insertValue.guidLowerNoBraces", "GUID lowercase without braces"),
    ];

    public static readonly IReadOnlyList<(string Id, string Caption)> CopyAsFormats =
    [
        ("copyAs.excelCsv", "Excel CSV"),
        ("copyAs.delimited", "Delimited text"),
        ("copyAs.html", "HTML table"),
        ("copyAs.xml", "XML"),
        ("copyAs.sqlInserts", "SQL INSERTs"),
        ("copyAs.sqlInsertIgnores", "SQL INSERT IGNOREs"),
        ("copyAs.sqlReplaces", "SQL REPLACEs"),
        ("copyAs.sqlDeletesInserts", "SQL DELETEs/INSERTs"),
        ("copyAs.sqlUpdates", "SQL UPDATEs"),
        ("copyAs.latex", "LaTeX"),
        ("copyAs.textile", "Textile"),
        ("copyAs.jiraTextile", "Jira Textile"),
        ("copyAs.phpArray", "PHP Array"),
        ("copyAs.markdown", "Markdown Here"),
        ("copyAs.json", "JSON"),
        ("copyAs.jsonLines", "JSON Lines"),
    ];

    private static readonly string[] Grid = [CommandContexts.Grid];
    private static readonly string[] Editor = [CommandContexts.QueryEditor];
    private static readonly string[] Filter = [CommandContexts.FilterPanel];
    private static readonly string[] TreeAndList = [CommandContexts.Tree, CommandContexts.ObjectList];
    private static readonly string[] Log = [CommandContexts.Log];
    private static readonly string[] HostList = [CommandContexts.HostList];
    private static readonly string[] Helpers = [CommandContexts.Helpers];

    public static CommandRegistry CreateRegistry()
    {
        var r = new CommandRegistry();
        Register(r);
        return r;
    }

    public static void Register(CommandRegistry r)
    {
        ArgumentNullException.ThrowIfNull(r);
        foreach (var command in Build())
        {
            r.Register(command);
        }
    }

    private static CommandDescriptor C(
        string id, string caption, string category, string? shortcut, string? action,
        string? icon = null, string? hint = null, string[]? contexts = null, string? availableIn = null,
        CommandKind kind = CommandKind.Action, string? radioGroup = null, bool hidden = false) =>
        new(id, caption, category, shortcut is null ? null : KeyChord.Parse(shortcut), contexts, icon, action, availableIn)
        {
            Hint = hint,
            Kind = kind,
            RadioGroup = radioGroup,
            Hidden = hidden,
        };

    private static IEnumerable<CommandDescriptor> Build()
    {
        // Ordem do ActionList1 (anexo C §2.1).
        yield return C("session.manager", "Session manager", "File", null, "actSessionManager", "session-manager", "Display session manager");
        yield return C("file.newWindow", "New window", "File", "Ctrl+N", "actNewWindow", "new-window", "New window...");
        yield return C("file.exit", "Exit", "File", "Alt+F4", "actExitApplication", "exit", "Exit application");
        yield return C("grid.followForeignKey", "Follow Foreign Key", "Various", null, "actFollowForeignKey", "follow-fk", "Follow foreign key to the linked table", Grid);
        yield return C("edit.copy", "Copy", "Various", "Ctrl+C", "actCopy", "copy", "Copy to Clipboard");
        yield return C("edit.paste", "Paste", "Various", "Ctrl+V", "actPaste", "paste", "Paste from Clipboard");
        yield return C("tools.userManager", "User manager", "Tools", null, "actUserManager", "user-manager", "Manage user authentication and privileges", availableIn: AfterMvp);
        yield return C("edit.cut", "Cut", "Various", "Ctrl+X", "actCut", "cut", "Cuts the selection and puts it on the Clipboard");
        yield return C("edit.undo", "Undo", "Various", "Alt+Backspace", "actUndo", "undo", "Revert last modification");
        yield return C("edit.copyTabsToSpaces", "Copy with tabs to spaces", "Various", "Ctrl+Shift+C", "actCopyTabsToSpaces", "copy");
        yield return C("help.about", "About...", "Various", null, "actAboutBox", "about", "About this application");
        yield return C("tools.maintenance", "Maintenance", "Tools", null, "actMaintenance", "maintenance", "Optimize, repair and analyse tables");
        yield return C("tools.findTextOnServer", "Find text on server", "Tools", "Ctrl+Shift+F", "actFindTextOnServer", "find-server", "Searches selected tables for text occurences");
        yield return C("export.gridRows", "Export grid rows", "Export/Import", null, "actExportData", "export-grid", "Export rows to file or copy to clipboard, in various formats");
        yield return C("list.print", "Print...", "Various", "Ctrl+P", "actPrintList", "print", "Print List or Data", availableIn: AfterMvp);
        yield return C("object.copyTable", "Table copy", "Database", null, "actCopyTable", "table-copy", "Create a base table copy of this table or view");
        yield return C("query.run", "Run", "SQL", "F9", "actExecuteQuery", "run", "Execute SQL-query/queries...");
        yield return C("query.runSelection", "Run Selection", "SQL", "Ctrl+F9", "actExecuteSelection", "run-selection", "Execute selected SQL-query/queries...");
        yield return C("query.runCurrent", "Run current query", "SQL", "Ctrl+Shift+F9", "actExecuteCurrentQuery", "run-current", "Run currently focused SQL query");
        yield return C("query.explainCurrent", "Explain current query", "SQL", null, "actExplainCurrentQuery", "explain", "Run EXPLAIN <current query> and show results");
        yield return C("grid.imagePreview", "Image preview", "Data", null, "actDataPreview", "image", "Preview image contents from BLOB cells", kind: CommandKind.Toggle);
        yield return C("import.filesIntoBlob", "Insert files into TEXT/BLOB fields...", "Export/Import", null, "actInsertFiles", "insert-files", availableIn: AfterMvp);
        yield return C("export.databaseAsSql", "Export database as SQL", "Export/Import", null, "actExportTables", "export-sql", "Dump database objects to an SQL file");
        yield return C("query.load", "Load SQL file...", "SQL", "Ctrl+O", "actLoadSQL", "open");
        yield return C("query.runFile", "Run SQL file...", "SQL", null, "actRunSQL", "run-file", "Run SQL file(s) directly, without loading into the editor");
        yield return C("object.drop", "Drop ...", "Database", null, "actDropObjects", "drop", "Deletes tables, views, procedures and functions", TreeAndList);
        yield return C("object.createView", "View", "Database", null, "actCreateView", "view", "Create view ...");
        yield return C("grid.first", "First", "Data", null, "actDataFirst", "first");
        yield return C("grid.last", "Last", "Data", null, "actDataLast", "last");
        yield return C("grid.insertRow", "Insert row", "Data", "Ins", "actDataInsert", "insert-row", "Insert row into table", Grid);
        yield return C("grid.duplicateRow", "Duplicate row without keys", "Data", "Ctrl+Ins", "actDataDuplicateRowWithoutKeys", "duplicate-row", contexts: Grid);
        yield return C("grid.duplicateRowWithKeys", "Duplicate row with keys", "Data", null, "actDataDuplicateRowWithKeys", "duplicate-row", contexts: Grid);
        yield return C("grid.deleteRows", "Delete selected row(s)", "Data", "Ctrl+Del", "actDataDelete", "delete-row", contexts: Grid);
        yield return C("grid.post", "Post", "Data", "Ctrl+Enter", "actDataPostChanges", "post", contexts: Grid);
        yield return C("grid.cancelEditing", "Cancel editing", "Data", "Esc", "actDataCancelChanges", "cancel-edit", contexts: Grid);
        yield return C("object.createTable", "Table", "Database", null, "actCreateTable", "table", "Create new table in selected database");
        yield return C("object.emptyTables", "Empty table(s) ...", "Database", "Shift+Del", "actEmptyTables", "empty-table", "Delete all rows in selected table(s)", TreeAndList);
        yield return C("object.createDatabase", "Database", "Database", null, "actCreateDatabase", "database", "Create a new, blank database");
        yield return C("help.sql", "SQL help", "Tools", "F1", "actSQLhelp", "help", "SQL help browser");
        yield return C("view.refresh", "Refresh", "Various", "F5", "actRefresh", "refresh");
        yield return C("view.fullRefresh", "Full status refresh", "Various", "Shift+F5", "actFullRefresh", "refresh", "Get full statistics refresh on table data. Slow on InnoDB tables!");
        yield return C("import.csv", "Import CSV file...", "Export/Import", null, "actImportCSV", "import-csv", "Import a CSV or tab delimited file");
        yield return C("settings.export", "Export settings file ...", "Export/Import", null, "actExportSettings", "export-settings");
        yield return C("settings.import", "Import settings file ...", "Export/Import", null, "actImportSettings", "import-settings");
        yield return C("tools.preferences", "Preferences", "Tools", null, "actPreferences", "preferences");
        // Não existe no HeidiSQL (roadmap 1.x #11): encontra qualquer comando pelo nome, sem mudar onde os outros estão.
        yield return C("tools.commandPalette", "Command palette ...", "Tools", "Ctrl+Shift+P", null, "find", "Find a command by name and run it");
        yield return C("tools.preferencesLogging", "Logging preferences", "Tools", null, "actPreferencesLogging", "preferences");
        yield return C("tools.preferencesData", "Data grid preferences", "Tools", null, "actPreferencesData", "preferences");
        yield return C("tools.flushHosts", "Hosts", "Tools", null, "actFlushHosts", availableIn: AfterMvp);
        yield return C("tools.flushLogs", "Logs", "Tools", null, "actFlushLogs", availableIn: AfterMvp);
        yield return C("tools.flushPrivileges", "Privileges", "Tools", null, "actFlushPrivileges", availableIn: AfterMvp);
        yield return C("tools.flushTables", "Tables", "Tools", null, "actFlushTables", availableIn: AfterMvp);
        yield return C("tools.flushTablesWithReadLock", "Tables with read lock", "Tools", null, "actFlushTableswithreadlock", availableIn: AfterMvp);
        yield return C("tools.flushStatus", "Status", "Tools", null, "actFlushStatus", availableIn: AfterMvp);
        yield return C("help.checkUpdates", "Check for updates ...", "Tools", null, "actUpdateCheck", "update", availableIn: AfterMvp);
        yield return C("help.downloadPage", "Download page", "Various", null, "actWebDownloadpage", "web");
        yield return C("help.forum", "Support forum", "Various", null, "actWebForum", "web");
        yield return C("help.changelog", "Changelog", "Various", null, "actWebChangelog", "web");
        yield return C("help.general", "General help", "Various", null, "actHelp", "help", "General online help document");
        yield return C("query.saveAs", "Save as ...", "SQL", "F12", "actSaveSQLAs", "save-as", "Save SQL to a textfile");
        yield return C("query.saveSelection", "Save selection to file ...", "SQL", "Ctrl+Shift+S", "actSaveSQLselection", "save-as", "Save selected text to a file");
        yield return C("query.saveSnippet", "Save as snippet ...", "SQL", null, "actSaveSQLSnippet", "save-snippet");
        yield return C("query.saveSelectionSnippet", "Save selection as snippet ...", "SQL", null, "actSaveSQLSelectionSnippet", "save-snippet", "Save selected text as snippet ...");
        yield return C("query.clear", "Clear", "SQL", "Ctrl+W", "actClearQueryEditor", "clear", "Clear query editor");
        yield return C("filter.clear", "Clear", "Data", "Ctrl+W", "actClearFilterEditor", "clear", "Clear filter editor", Filter);
        yield return C("log.clear", "Clear", "SQL", "Ctrl+Q", "actClearQueryLog", "clear", "Clear query log");
        yield return C("query.stopOnErrors", "Stop on errors in batch mode", "SQL", null, "actQueryStopOnErrors", "stop-on-errors", kind: CommandKind.Toggle);
        yield return C("query.wordWrap", "Wrap long lines", "SQL", null, "actQueryWordWrap", "word-wrap", kind: CommandKind.Toggle);
        yield return C("search.find", "Find text ...", "SQL", "Ctrl+F", "actQueryFind", "find");
        yield return C("search.replace", "Replace text ...", "SQL", "Ctrl+R", "actQueryReplace", "replace");
        yield return C("search.findAgain", "Find or replace again", "SQL", "F3", "actQueryFindAgain", "find-again");
        yield return C("query.setDelimiter", "Set delimiter used in SQL execution", "SQL", null, "actSetDelimiter", "delimiter");
        yield return C("filter.apply", "Apply filter", "Data", "F9", "actApplyFilter", "filter", contexts: Filter);
        yield return C("filter.remove", "Remove filter", "Data", null, "actRemoveFilter", "filter-remove", contexts: Grid);
        yield return C("goto.previousTab", "Previous tab", "Tools", "Ctrl+Shift+Tab", "actPreviousTab", "previous", "Go back to the previous tab");
        yield return C("goto.nextTab", "Next tab", "Tools", "Ctrl+Tab", "actNextTab", "next", "Go to the next tab");
        yield return C("edit.selectAll", "Select all", "Various", "Ctrl+A", "actSelectAll", "select-all", "Select all items or text");
        yield return C("object.createProcedure", "Stored procedure", "Database", null, "actCreateProcedure", "procedure", "Create stored procedure");
        yield return C("query.newTab", "New query tab", "File", "Ctrl+T", "actNewQueryTab", "new-tab", "Open a blank query tab");
        yield return C("query.newTabBackground", "New query tab in background", "File", null, "actNewQueryTabNofocus", "new-tab", hidden: true);
        yield return C("query.closeTab", "Close query tab", "File", "Ctrl+F4", "actCloseQueryTab", "close-tab");
        yield return C("edit.invertSelection", "Invert selection", "Various", "Ctrl+I", "actSelectInverse", "invert-selection");
        yield return C("display.filterPanel", "Filter panel", "Various", "Ctrl+Alt+F", "actFilterPanel", "filter", "Activates the filter panel", kind: CommandKind.Toggle);
        yield return C("tools.bulkTableEditor", "Bulk table editor", "Tools", null, "actBulkTableEdit", "bulk-edit", availableIn: AfterMvp);
        yield return C("object.createFunction", "Stored function", "Database", null, "actCreateFunction", "function", "Create stored function");
        yield return C("object.createTrigger", "Trigger", "Database", null, "actCreateTrigger", "trigger", "Create a trigger");
        yield return C("query.save", "Save", "SQL", "Ctrl+S", "actSaveSQL", "save", "Save SQL to file");
        yield return C("grid.resetSorting", "Reset sorting", "Data", "Alt+S", "actDataResetSorting", "sort-reset", contexts: Grid);
        yield return C("query.reformat", "Reformat SQL", "SQL", "Ctrl+F8", "actReformatSQL", "reformat", "Automatically reformat disordered SQL in active editor to make it more readable");
        yield return C("grid.blobAsText", "View binary data as text (instead of HEX)", "Data", null, "actBlobAsText", "blob-text", kind: CommandKind.Toggle);
        yield return C("data.showNext", "Next", "Data", "Ctrl+Alt+PgDn", "actDataShowNext", "next-rows", "Next X rows");
        yield return C("data.showAll", "Show all", "Data", "Ctrl+Alt+End", "actDataShowAll", "all-rows", "Show all rows");
        yield return C("object.runRoutines", "Run routine(s) ...", "Database", null, "actRunRoutines", "run", contexts: TreeAndList, availableIn: AfterMvp);
        yield return C("object.createEvent", "Event", "Database", null, "actCreateEvent", "event", "Create new event in selected database");
        yield return C("grid.setNull", "NULL", "Data", "Ctrl+Shift+N", "actDataSetNull", "null", "Set focused cell to NULL", Grid);
        yield return C("grid.saveBlob", "Save BLOB to file ...", "Data", null, "actDataSaveBlobToFile", "save", "Save contents to local file ...", Grid);
        yield return C("session.disconnect", "Disconnect", "File", null, "actDisconnect", "disconnect", "Close selected database connection");
        yield return C("query.batchInOneGo", "Send batch in one go", "SQL", null, "actBatchInOneGo", hint: "Send up to max_allowed_packet batch at once", kind: CommandKind.Radio, radioGroup: "sendMode");
        yield return C("query.singleQueries", "Send queries one by one", "SQL", null, "actSingleQueries", kind: CommandKind.Radio, radioGroup: "sendMode");
        yield return C("operation.cancel", "Cancel running operation", "Various", "Esc", "actCancelOperation", "cancel-operation");
        yield return C("query.toggleComment", "Un/comment", "SQL", null, "actToggleComment", "comment", "Makes selected SQL a comment or removes comment chars");
        yield return C("tools.generateData", "Generate data", "Tools", null, "actGenerateData", "generate", availableIn: AfterMvp);
        yield return C("tools.launchCommandLine", "Launch command line", "Tools", null, "actLaunchCommandline", "console", availableIn: AfterMvp);
        yield return C("grid.sqlFunction", "SQL function", "Data", "Ctrl+F2", "actGridEditFunction", "function", "Insert SQL function call in this grid cell, e.g. NOW()", Grid);
        yield return C("log.horizontalScrollbar", "Horizontal scrollbar", "Various", null, "actLogHorizontalScrollbar", kind: CommandKind.Toggle);
        yield return C("tree.groupObjects", "Group objects by type", "Various", null, "actGroupObjects", kind: CommandKind.Toggle);
        yield return C("grid.unixTimestamp", "This is a UNIX timestamp column", "Data", null, "actUnixTimestampColumn", contexts: Grid, availableIn: AfterMvp, kind: CommandKind.Toggle);
        yield return C("display.onlyFavorites", "Show only favorites", "Various", null, "actFavoriteObjectsOnly", "favorites", "Show only favorite tree items", kind: CommandKind.Toggle);
        yield return C("query.previousResult", "Previous result tab", "Data", "Alt+Left", "actPreviousResult", "previous");
        yield return C("query.nextResult", "Next result tab", "Data", "Alt+Right", "actNextResult", "next");
        yield return C("log.saveToFile", "Save as textfile...", "Various", "Ctrl+S", "actSaveSynMemoToTextfile", "save", "Save contents to a textfile", Log);
        yield return C("goto.tree", "Database tree", "Various", "Ctrl+D", "actGotoDbTree");
        yield return C("goto.tableFilter", "Table filter", "Various", "Ctrl+E", "actGotoFilter");
        for (var i = 1; i <= 5; i++)
        {
            yield return C($"goto.tab{i}", $"Tab {i}", "Various", $"Ctrl+{i}", $"actGotoTab{i}");
        }
        yield return C("goto.queryResults", "Switch to query/results", "Various", "F6", "actGoToQueryResults");
        yield return C("goto.multiColumnFilter", "Multi column filter", "Various", "F7", "actGoToDataMultiFilter");
        yield return C("grid.openUrl", "Open URL", "Data", null, "actDataOpenUrl", "web", "Open URL in your webbrowser", Grid);
        yield return C("object.detachDatabase", "Detach database ...", "Database", null, "actDetachDatabase", hint: "Detach attached database", availableIn: AfterMvp, hidden: true);
        yield return C("object.attachDatabase", "Attach database ...", "Database", null, "actAttachDatabase", hint: "Attach new or existing database file", availableIn: AfterMvp, hidden: true);
        yield return C("editor.completion", "Show SQL completion proposal", "SQL", "Ctrl+Space", "actSynEditCompletionPropose", contexts: Editor);
        foreach (var command in QuickFilters())
        {
            yield return command;
        }
        yield return C("query.codeFolding", "Enable code folding", "SQL", null, "actCodeFolding", "code-folding", "Enable code folding in SQL editors", kind: CommandKind.Toggle);
        yield return C("query.codeFoldingStartRegion", "Insert region start marker", "SQL", null, "actCodeFoldingStartRegion");
        yield return C("query.codeFoldingEndRegion", "Insert region end marker", "SQL", null, "actCodeFoldingEndRegion");
        yield return C("query.codeFoldingFoldSelection", "Fold selection", "SQL", null, "actCodeFoldingFoldSelection");
        yield return C("session.properties", "Connection properties", "Tools", null, "actConnectionProperties", "properties");
        yield return C("query.renameTab", "Rename query tab", "File", null, "actRenameQueryTab", "rename");
        yield return C("query.closeAllTabs", "Close all query tabs", "File", null, "actCloseAllQueryTabs", "close-tab");
        yield return C("editor.moveLineDown", "Move line down", "SQL", "Alt+Down", "actSynMoveDown", contexts: Editor);
        yield return C("editor.moveLineUp", "Move line up", "SQL", "Alt+Up", "actSynMoveUp", contexts: Editor);
        // Teclas do SynMemoQuery (anexo C §2.3): ecUpperCaseBlock e ecLowerCaseBlock.
        yield return C("editor.upperCase", "Upper case", "SQL", "Ctrl+U", null, contexts: Editor);
        yield return C("editor.lowerCase", "Lower case", "SQL", "Ctrl+L", null, contexts: Editor);
        yield return C("tools.sequalSuggest", "Sequal Suggest", "Tools", null, "actSequalSuggest", availableIn: AfterMvp, hidden: true);
        yield return C("display.resetPanelDimensions", "Reset panel dimensions", "Tools", null, "actResetPanelDimensions", hint: "Reset and fix overlapping panels in main window");
        yield return C("edit.copyGridNodes", "Copy all lines from listing or tree in CSV format", "Various", null, "actCopyGridNodes", "copy");
        yield return C("object.selectTopRows", "Select top 1000 rows", "Database", null, "actQueryTable", "run", "Selects the first 1000 rows in a new query tab", TreeAndList);
        // Schema Visualizer (secção 17.3): não existe no HeidiSQL.
        yield return C("diagram.show", "Show in schema visualizer", "Database", null, null, "diagram", "Shows the tables of the database as a diagram", TreeAndList);
        yield return C("display.objectSizes", "Display size of objects", "Various", null, "actDisplayObjectSize", kind: CommandKind.Toggle);
        yield return C("display.logPanel", "Log panel", "Various", null, "actDisplayLogPanel", kind: CommandKind.Toggle);
        yield return C("display.treeFilters", "Tree filters", "Various", null, "actDisplayTreeFilters", kind: CommandKind.Toggle);
        yield return C("edit.copyColumnNames", "Copy column names", "Various", null, "actCopyColumnNames", "copy");
        yield return C("edit.copyFormatted", "Copy formatted text", "Various", null, "actCopyFormatted", "copy", "Copies selected text with formatting from current editor to clipboard");
        yield return C("grid.editWithoutLookup", "Edit value without foreign key lookup", "Data", null, "actDataEditWithoutLookup", contexts: Grid);
        yield return C("display.columnsInTree", "Columns below tables in tree", "Various", null, "actTreeShowColumns", kind: CommandKind.Toggle);

        // As 16 "Copy as" (criadas em runtime no HeidiSQL, categoria Export/Import, sem atalho).
        foreach (var (id, caption) in CopyAsFormats)
        {
            yield return C(id, caption, "Export/Import", null, null, "copy", contexts: Grid);
        }

        // Atalhos definidos em itens de menu, não em actions (anexo C §2.2).
        yield return C("object.edit", "Edit", "Database", "Alt+Enter", null, "edit", contexts: TreeAndList);
        yield return C("host.killProcess", "Kill Process(es)...", "Various", "Del", null, "kill", contexts: HostList);
        yield return C("host.editVariable", "Edit ...", "Various", "Enter", null, "edit", contexts: HostList, availableIn: AfterMvp);
        yield return C("host.fetchDatabaseItems", "Fetch database items", "Various", null, null, "refresh", contexts: HostList);
        yield return C("host.explainProcess", "EXPLAIN query", "Various", null, null, "explain", contexts: HostList);
        yield return C("helpers.deleteSnippet", "Delete ...", "SQL", "Del", null, "delete", contexts: Helpers);
        // popupQueryHelpers (anexo C §3.9).
        yield return C("helpers.insert", "Insert at cursor", "SQL", null, null, contexts: Helpers);
        yield return C("helpers.loadSnippet", "Load", "SQL", null, null, "open", contexts: Helpers);
        yield return C("helpers.renameSnippet", "Rename", "SQL", null, null, "rename", contexts: Helpers);
        yield return C("helpers.exploreFolder", "Explore folder", "SQL", null, null, contexts: Helpers);
        yield return C("helpers.clearHistory", "Clear query history ...", "SQL", null, null, "clear", contexts: Helpers);
        yield return C("helpers.generateSelect", "Generate SELECT ...", "SQL", null, null, contexts: Helpers);
        yield return C("helpers.generateInsert", "Generate INSERT ...", "SQL", null, null, contexts: Helpers);
        yield return C("helpers.generateUpdate", "Generate UPDATE ...", "SQL", null, null, contexts: Helpers);
        yield return C("helpers.generateDelete", "Generate DELETE ...", "SQL", null, null, contexts: Helpers);
        yield return C("log.copyLineToNewTab", "Copy line to new query tab", "Various", null, null, "new-tab", contexts: Log);
        // popupDB › Tree style options (anexo C §3.3): itens sem action.
        // popupDataGrid › Insert value (anexo C §3.5): itens sem action; a edição chega na Fase 7.
        foreach (var (id, caption) in InsertValues)
        {
            yield return C(id, caption, "Data", null, null, contexts: Grid);
        }
        yield return C("object.clearDataTabFilter", "Clear data tab filter and sort order", "Database", null, null, contexts: TreeAndList);
        yield return C("tree.expandAll", "Expand all", "Various", null, null, null);
        yield return C("tree.collapseAll", "Collapse all", "Various", null, null, null);
        yield return C("tree.autoExpand", "Auto expand on click", "Various", null, null, kind: CommandKind.Toggle);
        yield return C("tree.doubleClickInsertsNodeText", "Doubleclick inserts node text", "Various", null, null, kind: CommandKind.Toggle);
        // popupMainTabs (anexo C §3.13): itens sem action.
        yield return C("tabs.closeToRight", "Close query tabs to the right", "File", null, null, "close-tab");
        yield return C("tabs.closeOnDoubleClick", "Close tab on doubleclick", "Various", null, null, kind: CommandKind.Toggle);
        yield return C("tabs.closeOnMiddleClick", "Close tab on middleclick", "Various", null, null, kind: CommandKind.Toggle);
        yield return C("tabs.multipleLines", "Tabs in multiple lines", "Various", null, null, kind: CommandKind.Toggle);
        yield return C("query.pinToSession", "Pin tab to session", "SQL", null, null, "pin", "Always run this query tab on its current session (D9)", kind: CommandKind.Toggle);
        yield return C("view.autoRefresh", "Auto refresh", "Various", "Ctrl+F5", null, kind: CommandKind.Toggle);
        yield return C("view.autoRefreshInterval", "Set interval ...", "Various", null, null);
    }

    private static IEnumerable<CommandDescriptor> QuickFilters()
    {
        // Quick Filter (FAM-G13): captions finais são dinâmicas (SQL real); estas são as do Shortcuts.
        string[] focused = ["Column = Focused", "Column != Focused", "Column LIKE Focused%", "Column LIKE %Focused", "Column LIKE %Focused%", "Column > Focused", "Column < Focused"];
        string?[] focusedKeys = ["Ctrl+Shift+1", "Ctrl+Shift+2", null, null, null, null, null];
        for (var i = 0; i < focused.Length; i++)
        {
            yield return C($"quickFilter.focused{i + 1}", "Quick filter: " + focused[i], "Data", focusedKeys[i], $"actQuickFilterFocused{i + 1}", "filter", contexts: Grid);
        }
        string[] prompt = ["Column = Prompt", "Column != Prompt", "Column > Prompt", "Column < Prompt", "Column LIKE %Prompt%"];
        string?[] promptKeys = ["Ctrl+Shift+3", "Ctrl+Shift+4", null, null, null];
        for (var i = 0; i < prompt.Length; i++)
        {
            yield return C($"quickFilter.prompt{i + 1}", "Quick filter: " + prompt[i], "Data", promptKeys[i], $"actQuickFilterPrompt{i + 1}", "filter", contexts: Grid);
        }
        yield return C("quickFilter.null", "Quick filter: Column IS NULL", "Data", null, "actQuickFilterNull", "filter", contexts: Grid);
        yield return C("quickFilter.notNull", "Quick filter: Column IS NOT NULL", "Data", null, "actQuickFilterNotNull", "filter", contexts: Grid);
        string[] clip = ["Column = Clipboard", "Column != Clipboard", "Column > Clipboard", "Column < Clipboard", "Column LIKE %Clipboard%", "Column IN (Clipboard)"];
        string?[] clipKeys = ["Ctrl+Shift+5", "Ctrl+Shift+6", null, null, null, null];
        for (var i = 0; i < clip.Length; i++)
        {
            yield return C($"quickFilter.clipboard{i + 1}", "Quick filter: " + clip[i], "Data", clipKeys[i], $"actQuickFilterClipboard{i + 1}", "filter", contexts: Grid);
        }
    }
}
