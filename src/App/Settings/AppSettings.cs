namespace Eunomia.App.Settings;

/// <summary>
/// Global application settings (section 21.4): versioned JSON in <c>%APPDATA%\Eunomia\settings.json</c>, written
/// atomically. Default values are HeidiSQL's (appendix C, §1). Secrets never come here.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Format version. Goes up when a field changes meaning; see <c>SettingsMigrations</c>.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public WindowGeometry MainWindow { get; set; } = new();

    public PanelLayout Panels { get; set; } = new();

    public DisplayOptions Display { get; set; } = new();

    /// <summary><see cref="ThemeNames"/>: light, dark or the system's.</summary>
    public string Theme { get; set; } = ThemeNames.Light;

    /// <summary>
    /// Redefined shortcuts (Preferences › Shortcuts, FAM-K01): command id → shortcut ("Ctrl+Shift+F9"), or empty
    /// text when the user removed the shortcut. Missing commands use the default shortcut.
    /// </summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];

    public LogOptions Log { get; set; } = new();

    public TabOptions Tabs { get; set; } = new();

    public QueryOptions Query { get; set; } = new();

    public DataOptions Data { get; set; } = new();

    public SessionManagerOptions SessionManager { get; set; } = new();

    public TreeOptions Tree { get; set; } = new();

    public DialogOptions Dialogs { get; set; } = new();

    public ExportOptions Export { get; set; } = new();

    public SearchSettings Search { get; set; } = new();

    public SqlExportSettings SqlExport { get; set; } = new();

    public CsvImportSettings CsvImport { get; set; } = new();

    /// <summary>Visible columns of the lists (Database tab…), by list key.</summary>
    public Dictionary<string, List<string>> Lists { get; set; } = [];
}

public static class ThemeNames
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";
}

/// <summary>Position and size of the main window (HeidiSQL's <c>MainWin*</c>: 100, 100, 950 × 600).</summary>
public sealed class WindowGeometry
{
    public int Left { get; set; } = 100;

    public int Top { get; set; } = 100;

    public int Width { get; set; } = 950;

    public int Height { get; set; } = 600;

    public bool Maximized { get; set; }
}

/// <summary>
/// Panel dimensions (section 5.1, FAM-L07), in logical pixels (96 DPI). <see cref="Reset"/> is
/// Display › Reset panel dimensions.
/// </summary>
public sealed class PanelLayout
{
    public const int DefaultTreeWidth = 270;
    public const int DefaultLogHeight = 80;
    public const int DefaultQueryEditorHeight = 100;
    public const int DefaultQueryHelpersWidth = 200;
    public const int DefaultPreviewHeight = 100;

    /// <summary><c>dbtreewidth</c>.</summary>
    public int TreeWidth { get; set; } = DefaultTreeWidth;

    /// <summary><c>sqloutheight</c>.</summary>
    public int LogHeight { get; set; } = DefaultLogHeight;

    /// <summary><c>querymemoheight</c>.</summary>
    public int QueryEditorHeight { get; set; } = DefaultQueryEditorHeight;

    /// <summary><c>queryhelperswidth</c>.</summary>
    public int QueryHelpersWidth { get; set; } = DefaultQueryHelpersWidth;

    /// <summary><c>DataPreviewHeight</c>.</summary>
    public int PreviewHeight { get; set; } = DefaultPreviewHeight;

    public void Reset()
    {
        TreeWidth = DefaultTreeWidth;
        LogHeight = DefaultLogHeight;
        QueryEditorHeight = DefaultQueryEditorHeight;
        QueryHelpersWidth = DefaultQueryHelpersWidth;
        PreviewHeight = DefaultPreviewHeight;
    }
}

/// <summary>Display menu toggles (appendix C, §1.5) with HeidiSQL's defaults.</summary>
public sealed class DisplayOptions
{
    public bool ObjectSizes { get; set; } = true;

    public bool FilterPanel { get; set; } = true;

    public bool LogPanel { get; set; } = true;

    public bool TreeFilters { get; set; } = true;

    public bool ColumnsInTree { get; set; }

    /// <summary>Bars on the numeric columns of the Host and Database tab lists (<c>DisplayBars</c>).</summary>
    public bool Bars { get; set; } = true;
}

/// <summary>SQL log (appendix C, §1.10): <c>logsqlnum</c>, <c>logsqlwidth</c> and categories.</summary>
public sealed class LogOptions
{
    public int MaxLines { get; set; } = 300;

    public int MaxLineLength { get; set; } = 2000;

    public bool Timestamps { get; set; }

    public bool HorizontalScrollbar { get; set; }

    public bool Errors { get; set; } = true;

    public bool UserSql { get; set; } = true;

    public bool InternalSql { get; set; } = true;

    public bool Script { get; set; }

    public bool Info { get; set; } = true;

    public bool Debug { get; set; }
}

/// <summary>Main tabs behaviour (<c>popupMainTabs</c>, appendix C §1.8).</summary>
public sealed class TabOptions
{
    public bool CloseOnDoubleClick { get; set; } = true;

    public bool CloseOnMiddleClick { get; set; } = true;

    public bool MultipleLines { get; set; } = true;
}

/// <summary>Query tab toggles (toolbar and Query menu).</summary>
public sealed class QueryOptions
{
    /// <summary><c>actQueryStopOnErrors</c>, on by default.</summary>
    public bool StopOnErrors { get; set; } = true;

    public bool WordWrap { get; set; }

    /// <summary>On by default (HeidiSQL's hidden <c>CodeFolding</c> setting).</summary>
    public bool CodeFolding { get; set; } = true;

    /// <summary>Send batch in one go (radio with "Send queries one by one", which is the default).</summary>
    public bool BatchInOneGo { get; set; }

    /// <summary><c>WarnUnsafeUpdates</c>: ask before UPDATE/DELETE without WHERE.</summary>
    public bool WarnUnsafeUpdates { get; set; } = true;

    /// <summary><c>MaxQueryResults</c>: result tabs per run.</summary>
    public int MaxResults { get; set; } = 10;

    /// <summary><c>Delimiter</c>: statement delimiter ("Set delimiter used in SQL execution").</summary>
    public string Delimiter { get; set; } = ";";

    /// <summary>"Enable query history", on by default.</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>"Reopen previously used SQL files and unsaved content in tabs", on by default (needs a restart).</summary>
    public bool RestoreTabs { get; set; } = true;

    /// <summary>"Prompt to save modified files on tab close", on by default.</summary>
    public bool PromptSaveOnClose { get; set; } = true;

    /// <summary><c>SQLFile0..19</c>: recent SQL files, the latest first.</summary>
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>Preferences › SQL: editor font (fixed-width only) and size in points.</summary>
    public string EditorFont { get; set; } = "Consolas";

    public int EditorFontSize { get; set; } = 9;

    public int TabWidth { get; set; } = 3;

    public bool TabsToSpaces { get; set; }

    /// <summary>"Auto completion proposal": on, 500 ms after <c>.</c>.</summary>
    public bool CompletionEnabled { get; set; } = true;

    public int CompletionDelayMs { get; set; } = 500;

    /// <summary>"Auto uppercase reserved words and functions".</summary>
    public bool AutoUppercase { get; set; } = true;

    /// <summary>Snippets folder; empty = <c>Documents\Eunomia\Snippets</c>.</summary>
    public string? SnippetsDirectory { get; set; }
}

/// <summary>Data grid options.</summary>
public sealed class DataOptions
{
    /// <summary><c>actBlobAsText</c>: binaries as text instead of HEX.</summary>
    public bool BlobAsText { get; set; }

    /// <summary><c>ForeignDropDown</c>: list of the referenced table's values when editing a column with a foreign key.</summary>
    public bool ForeignDropDown { get; set; } = true;

    /// <summary><c>DatagridRowsPerStep</c>: rows per step of the Data tab and of "Select top N rows".</summary>
    public int RowsPerStep { get; set; } = 1000;

    /// <summary><c>DatagridMaximumRows</c>: Show all limit (D5: reaching it shows in the label).</summary>
    public int MaximumRows { get; set; } = 100_000;

    /// <summary>Memory budget per result in MB (section 11.3); above it, spill to disk.</summary>
    public int ResultMemoryMb { get; set; } = 256;

    /// <summary>Disk spill quota per result, in MB.</summary>
    public int SpillQuotaMb { get; set; } = 10 * 1024;

    /// <summary>Global budget for all results in MB (PR-17); above it, the largest ones spill.</summary>
    public int GlobalResultMemoryMb { get; set; } = 1024;

    /// <summary>Maximum column width for automatic sizing, in pixels (<c>MaxColWidth</c>).</summary>
    public int MaxColumnWidth { get; set; } = 300;

    /// <summary>Grid font (<c>DataFontName</c>).</summary>
    public string FontName { get; set; } = "Tahoma";

    /// <summary>Grid font size in points (<c>DataFontSize</c>).</summary>
    public int FontSize { get; set; } = 8;
}

/// <summary>Session Manager (appendix A §1.1–1.2, §8): window, list width, columns, folders at top, last session.</summary>
public sealed class SessionManagerOptions
{
    public int WindowWidth { get; set; } = 749;

    public int WindowHeight { get; set; } = 506;

    /// <summary><c>SessionManager_ListWidth</c>.</summary>
    public int ListWidth { get; set; } = 220;

    /// <summary><c>SessionManager_ListFoldersAtTop</c>.</summary>
    public bool FoldersAtTop { get; set; } = true;

    /// <summary>Widths per column (Session name, Host, Port, User, Version, Last connect, Counter, Comment).</summary>
    public List<int> ColumnWidths { get; set; } = [165, 100, 50, 80, 80, 120, 60, 120];

    /// <summary>Visible columns (User, Version and Counter hidden by default).</summary>
    public List<bool> ColumnsVisible { get; set; } = [true, true, true, false, false, true, false, true];

    public int SortColumn { get; set; }

    public bool SortDescending { get; set; }

    /// <summary><c>LastActiveSession</c>: id of the last session opened.</summary>
    public Guid? LastActiveSession { get; set; }

    /// <summary><c>LastSessions</c>: sessions open at shutdown (for "Automatically reconnect").</summary>
    public List<Guid> LastSessions { get; set; } = [];

    /// <summary>"Automatically reconnect to previously opened sessions on startup" (off).</summary>
    public bool AutoReconnect { get; set; }

    /// <summary>Each session's last database used is selected again on connecting (<c>RestoreLastUsedDB</c>).</summary>
    public bool RestoreLastUsedDatabase { get; set; } = true;

    /// <summary>On first start the user was already asked whether to import the sessions of the installed HeidiSQL.</summary>
    public bool HeidiImportOffered { get; set; }
}

/// <summary>
/// Tree style options (appendix D A3) with HeidiSQL's defaults. "Display size of objects" and "Columns below tables in
/// tree" live in <see cref="DisplayOptions"/> (they are also in the Display menu); "Show only favorites" is not saved.
/// </summary>
public sealed class TreeOptions
{
    public bool GroupObjects { get; set; }

    public bool AutoExpand { get; set; }

    public bool DoubleClickInsertsNodeText { get; set; } = true;
}

/// <summary>Values remembered by the dialogs (<c>asCreateDbCollation</c>…).</summary>
public sealed class DialogOptions
{
    /// <summary>Last collation chosen in Create database.</summary>
    public string? CreateDatabaseCollation { get; set; }
}

/// <summary>Last state of "Search and replace text" (appendix C §4.7): recent entries and options.</summary>
public sealed class SearchSettings
{
    /// <summary><c>FindDialogSearchHistory</c>.</summary>
    public List<string> FindHistory { get; set; } = [];

    public List<string> ReplaceHistory { get; set; } = [];

    public bool CaseSensitive { get; set; }

    public bool WholeWords { get; set; }

    public bool RegularExpression { get; set; }

    public bool PromptOnReplace { get; set; } = true;

    public bool Backward { get; set; }

    public bool EntireScope { get; set; } = true;
}

/// <summary>
/// SQL export (appendix B §2.3.1; HeidiSQL's <c>ExportSQL_*</c>): Create of databases and tables, data mode, Max
/// INSERT size, output and the options. The Drop options are not saved (for safety, as in HeidiSQL).
/// </summary>
public sealed class SqlExportSettings
{
    public bool CreateDatabases { get; set; }

    public bool CreateTables { get; set; }

    /// <summary>0 No data, 1 Delete + insert, 2 Insert, 3 Insert ignore, 4 Replace.</summary>
    public int Data { get; set; }

    public int MaxInsertSizeKb { get; set; } = 1024;

    /// <summary>0 file, 1 ZIP, 2 directory, 3 clipboard.</summary>
    public int Output { get; set; }

    /// <summary>Recent entries (up to 20) per output kind.</summary>
    public List<string> Filenames { get; set; } = [];

    public List<string> ZipFilenames { get; set; } = [];

    public List<string> Directories { get; set; } = [];

    public bool AddComments { get; set; } = true;

    public bool WrapInTransactions { get; set; }

    public bool RemoveAutoIncrement { get; set; }

    public bool RemoveDefiner { get; set; } = true;

    /// <summary>New option (section 13.2): a consistent snapshot on one connection instead of tables in parallel.</summary>
    public bool ConsistentSnapshot { get; set; }

    /// <summary>Filters (regular expressions) of the dialog's tree.</summary>
    public string DatabaseFilter { get; set; } = string.Empty;

    public string TableFilter { get; set; } = string.Empty;
}

/// <summary>
/// Import CSV file (appendix B §3.1; HeidiSQL defaults in <c>apphelpers.pas</c>): separators with backslash sequences as
/// the user types them (<c>\r\n</c>), REPLACE and the server by default. Truncate is not saved.
/// </summary>
public sealed class CsvImportSettings
{
    public string FileName { get; set; } = string.Empty;

    public string Encoding { get; set; } = "Auto detect (may fail)";

    public int IgnoreLines { get; set; } = 1;

    public bool LowPriority { get; set; } = true;

    public bool LocalNumbers { get; set; }

    public bool KeepDialogOpen { get; set; }

    public string FieldTerminator { get; set; } = ";";

    public string FieldEncloser { get; set; } = "\"";

    public bool OptionallyEnclosed { get; set; } = true;

    public string FieldEscaper { get; set; } = "\"";

    public string LineTerminator { get; set; } = "\\r\\n";

    /// <summary>0 INSERT, 1 INSERT IGNORE, 2 REPLACE.</summary>
    public int Duplicates { get; set; } = 2;

    /// <summary>0 server (LOAD DATA), 1 client.</summary>
    public int Method { get; set; }
}

public sealed class ExportOptions
{
    public int Format { get; set; }

    public bool OutputToClipboard { get; set; } = true;

    public string FileName { get; set; } = string.Empty;

    public List<string> RecentFiles { get; set; } = [];

    public string Encoding { get; set; } = "UTF-8";

    public bool SelectedRowsOnly { get; set; }

    public bool ColumnNames { get; set; } = true;

    public bool AutoIncrement { get; set; } = true;

    public bool FocusedColumnOnly { get; set; }

    public bool IncludeQuery { get; set; }

    public bool RemoveLinebreaks { get; set; }

    public bool OpenFile { get; set; }

    public string Separator { get; set; } = ";";

    public string Encloser { get; set; } = string.Empty;

    public string Terminator { get; set; } = @"\r\n";

    public string NullValue { get; set; } = @"\N";

    public bool ClipboardColumnNames { get; set; } = true;

    public bool ClipboardAutoIncrement { get; set; } = true;

    public bool ClipboardRemoveLinebreaks { get; set; }

    public string ClipboardSeparator { get; set; } = ";";

    public string ClipboardEncloser { get; set; } = string.Empty;

    public string ClipboardTerminator { get; set; } = @"\r\n";

    public string ClipboardNullValue { get; set; } = @"\N";
}
