namespace Eunomia.App.Settings;

/// <summary>
/// Settings globais da aplicação (secção 21.4): JSON versionado em <c>%APPDATA%\Eunomia\settings.json</c>, escrito
/// de forma atómica. Os valores por omissão são os do HeidiSQL (anexo C, §1). Segredos nunca vêm para aqui.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Versão do formato. Sobe quando um campo muda de significado; ver <c>SettingsMigrations</c>.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public WindowGeometry MainWindow { get; set; } = new();

    public PanelLayout Panels { get; set; } = new();

    public DisplayOptions Display { get; set; } = new();

    /// <summary><see cref="ThemeNames"/>: claro, escuro ou o do sistema.</summary>
    public string Theme { get; set; } = ThemeNames.Light;

    /// <summary>
    /// Atalhos redefinidos (Preferences › Shortcuts, FAM-K01): id do comando → atalho ("Ctrl+Shift+F9"), ou texto
    /// vazio quando o utilizador removeu o atalho. Comandos ausentes usam o atalho por omissão.
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

    /// <summary>Colunas visíveis das listas (Database tab…), por chave da lista.</summary>
    public Dictionary<string, List<string>> Lists { get; set; } = [];
}

public static class ThemeNames
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";
}

/// <summary>Posição e tamanho da janela principal (<c>MainWin*</c> do HeidiSQL: 100, 100, 950 × 600).</summary>
public sealed class WindowGeometry
{
    public int Left { get; set; } = 100;

    public int Top { get; set; } = 100;

    public int Width { get; set; } = 950;

    public int Height { get; set; } = 600;

    public bool Maximized { get; set; }
}

/// <summary>
/// Dimensões dos painéis (secção 5.1, FAM-L07), em pixels lógicos (96 DPI). <see cref="Reset"/> é o
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

/// <summary>Toggles do menu Display (anexo C, §1.5) com os defaults do HeidiSQL.</summary>
public sealed class DisplayOptions
{
    public bool ObjectSizes { get; set; } = true;

    public bool FilterPanel { get; set; } = true;

    public bool LogPanel { get; set; } = true;

    public bool TreeFilters { get; set; } = true;

    public bool ColumnsInTree { get; set; }

    /// <summary>Barras nas colunas numéricas das listas do Host e do Database tab (<c>DisplayBars</c>).</summary>
    public bool Bars { get; set; } = true;
}

/// <summary>SQL log (anexo C, §1.10): <c>logsqlnum</c>, <c>logsqlwidth</c> e categorias.</summary>
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

/// <summary>Comportamento das tabs principais (<c>popupMainTabs</c>, anexo C §1.8).</summary>
public sealed class TabOptions
{
    public bool CloseOnDoubleClick { get; set; } = true;

    public bool CloseOnMiddleClick { get; set; } = true;

    public bool MultipleLines { get; set; } = true;
}

/// <summary>Toggles do Query tab (toolbar e menu Query).</summary>
public sealed class QueryOptions
{
    /// <summary><c>actQueryStopOnErrors</c>, ligado por omissão.</summary>
    public bool StopOnErrors { get; set; } = true;

    public bool WordWrap { get; set; }

    /// <summary>Ligado por omissão (setting escondida <c>CodeFolding</c> do HeidiSQL).</summary>
    public bool CodeFolding { get; set; } = true;

    /// <summary>Send batch in one go (rádio com "Send queries one by one", que é a omissão).</summary>
    public bool BatchInOneGo { get; set; }

    /// <summary><c>WarnUnsafeUpdates</c>: perguntar antes de UPDATE/DELETE sem WHERE.</summary>
    public bool WarnUnsafeUpdates { get; set; } = true;

    /// <summary><c>MaxQueryResults</c>: result tabs por execução.</summary>
    public int MaxResults { get; set; } = 10;

    /// <summary><c>Delimiter</c>: delimitador das instruções ("Set delimiter used in SQL execution").</summary>
    public string Delimiter { get; set; } = ";";

    /// <summary>"Enable query history", ligado por omissão.</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>"Reopen previously used SQL files and unsaved content in tabs", ligado por omissão (precisa de reiniciar).</summary>
    public bool RestoreTabs { get; set; } = true;

    /// <summary>"Prompt to save modified files on tab close", ligado por omissão.</summary>
    public bool PromptSaveOnClose { get; set; } = true;

    /// <summary><c>SQLFile0..19</c>: ficheiros SQL recentes, o último primeiro.</summary>
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>Preferences › SQL: fonte do editor (só de largura fixa) e tamanho em pontos.</summary>
    public string EditorFont { get; set; } = "Consolas";

    public int EditorFontSize { get; set; } = 9;

    public int TabWidth { get; set; } = 3;

    public bool TabsToSpaces { get; set; }

    /// <summary>"Auto completion proposal": ligado, com 500 ms depois de <c>.</c>.</summary>
    public bool CompletionEnabled { get; set; } = true;

    public int CompletionDelayMs { get; set; } = 500;

    /// <summary>"Auto uppercase reserved words and functions".</summary>
    public bool AutoUppercase { get; set; } = true;

    /// <summary>Pasta dos snippets; vazia = <c>Documents\Eunomia\Snippets</c>.</summary>
    public string? SnippetsDirectory { get; set; }
}

/// <summary>Opções do grid de dados.</summary>
public sealed class DataOptions
{
    /// <summary><c>actBlobAsText</c>: binários como texto em vez de HEX.</summary>
    public bool BlobAsText { get; set; }

    /// <summary><c>ForeignDropDown</c>: lista dos valores da tabela referida ao editar uma coluna com chave estrangeira.</summary>
    public bool ForeignDropDown { get; set; } = true;

    /// <summary><c>DatagridRowsPerStep</c>: linhas por passo do Data tab e do "Select top N rows".</summary>
    public int RowsPerStep { get; set; } = 1000;

    /// <summary><c>DatagridMaximumRows</c>: limite do Show all (D5: atingi-lo aparece na label).</summary>
    public int MaximumRows { get; set; } = 100_000;

    /// <summary>Orçamento de memória por resultado em MB (secção 11.3); acima disso, spill para disco.</summary>
    public int ResultMemoryMb { get; set; } = 256;

    /// <summary>Quota do spill em disco por resultado, em MB.</summary>
    public int SpillQuotaMb { get; set; } = 10 * 1024;

    /// <summary>Orçamento global de todos os resultados em MB (PR-17); acima disso, os maiores vão para o spill.</summary>
    public int GlobalResultMemoryMb { get; set; } = 1024;

    /// <summary>Largura máxima das colunas no ajuste automático, em pixels (<c>MaxColWidth</c>).</summary>
    public int MaxColumnWidth { get; set; } = 300;

    /// <summary>Fonte do grid (<c>DataFontName</c>).</summary>
    public string FontName { get; set; } = "Tahoma";

    /// <summary>Tamanho da fonte do grid em pontos (<c>DataFontSize</c>).</summary>
    public int FontSize { get; set; } = 8;
}

/// <summary>Session Manager (anexo A §1.1–1.2, §8): janela, largura da lista, colunas, folders no topo, última sessão.</summary>
public sealed class SessionManagerOptions
{
    public int WindowWidth { get; set; } = 749;

    public int WindowHeight { get; set; } = 506;

    /// <summary><c>SessionManager_ListWidth</c>.</summary>
    public int ListWidth { get; set; } = 220;

    /// <summary><c>SessionManager_ListFoldersAtTop</c>.</summary>
    public bool FoldersAtTop { get; set; } = true;

    /// <summary>Larguras por coluna (Session name, Host, Port, User, Version, Last connect, Counter, Comment).</summary>
    public List<int> ColumnWidths { get; set; } = [165, 100, 50, 80, 80, 120, 60, 120];

    /// <summary>Colunas visíveis (User, Version e Counter ocultas por omissão).</summary>
    public List<bool> ColumnsVisible { get; set; } = [true, true, true, false, false, true, false, true];

    public int SortColumn { get; set; }

    public bool SortDescending { get; set; }

    /// <summary><c>LastActiveSession</c>: id da última sessão aberta.</summary>
    public Guid? LastActiveSession { get; set; }

    /// <summary><c>LastSessions</c>: sessões abertas no fecho (para "Automatically reconnect").</summary>
    public List<Guid> LastSessions { get; set; } = [];

    /// <summary>"Automatically reconnect to previously opened sessions on startup" (off).</summary>
    public bool AutoReconnect { get; set; }

    /// <summary>A última base usada de cada sessão volta a ficar selecionada ao ligar (<c>RestoreLastUsedDB</c>).</summary>
    public bool RestoreLastUsedDatabase { get; set; } = true;

    /// <summary>Já se perguntou, no primeiro arranque, se importa as sessões do HeidiSQL instalado.</summary>
    public bool HeidiImportOffered { get; set; }
}

/// <summary>
/// Tree style options (anexo D A3) com os defaults do HeidiSQL. "Display size of objects" e "Columns below tables in
/// tree" ficam em <see cref="DisplayOptions"/> (também estão no menu Display); "Show only favorites" não é gravado.
/// </summary>
public sealed class TreeOptions
{
    public bool GroupObjects { get; set; }

    public bool AutoExpand { get; set; }

    public bool DoubleClickInsertsNodeText { get; set; } = true;
}

/// <summary>Valores lembrados pelos diálogos (<c>asCreateDbCollation</c>…).</summary>
public sealed class DialogOptions
{
    /// <summary>Última collation escolhida no Create database.</summary>
    public string? CreateDatabaseCollation { get; set; }
}

/// <summary>
/// Export grid rows e Copy as (anexo B §1.4, §1.5): as opções do diálogo (<c>GridExport*</c>) e as do clipboard
/// (<c>GridExportClp*</c>), separadas como no HeidiSQL.
/// </summary>
/// <summary>Último estado do "Search and replace text" (anexo C §4.7): recentes e opções.</summary>
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
/// SQL export (anexo B §2.3.1; <c>ExportSQL_*</c> do HeidiSQL): Create das bases e das tabelas, modo dos dados, Max
/// INSERT size, output e as opções. Os Drop não se gravam (por segurança, como no HeidiSQL).
/// </summary>
public sealed class SqlExportSettings
{
    public bool CreateDatabases { get; set; }

    public bool CreateTables { get; set; }

    /// <summary>0 No data, 1 Delete + insert, 2 Insert, 3 Insert ignore, 4 Replace.</summary>
    public int Data { get; set; }

    public int MaxInsertSizeKb { get; set; } = 1024;

    /// <summary>0 ficheiro, 1 ZIP, 2 diretório, 3 clipboard.</summary>
    public int Output { get; set; }

    /// <summary>Recentes (até 20) por tipo de output.</summary>
    public List<string> Filenames { get; set; } = [];

    public List<string> ZipFilenames { get; set; } = [];

    public List<string> Directories { get; set; } = [];

    public bool AddComments { get; set; } = true;

    public bool WrapInTransactions { get; set; }

    public bool RemoveAutoIncrement { get; set; }

    public bool RemoveDefiner { get; set; } = true;

    /// <summary>Opção nova (secção 13.2): snapshot consistente numa ligação em vez de tabelas em paralelo.</summary>
    public bool ConsistentSnapshot { get; set; }

    /// <summary>Filtros (expressões regulares) da árvore do diálogo.</summary>
    public string DatabaseFilter { get; set; } = string.Empty;

    public string TableFilter { get; set; } = string.Empty;
}

/// <summary>
/// Import CSV file (anexo B §3.1; defaults do HeidiSQL em <c>apphelpers.pas</c>): separadores com sequências de barra
/// invertida como o utilizador as escreve (<c>\r\n</c>), REPLACE e o servidor por omissão. O Truncate não se grava.
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

    /// <summary>0 servidor (LOAD DATA), 1 cliente.</summary>
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
