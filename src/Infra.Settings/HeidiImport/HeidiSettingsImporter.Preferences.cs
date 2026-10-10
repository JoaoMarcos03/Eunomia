using System.Globalization;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;

namespace Eunomia.Infra.Settings.HeidiImport;

/// <summary>
/// Preferências e estado do HeidiSQL com equivalente no Eunomia (Fase 13; anexo A §8, anexo C §7, anexo D B9/B12):
/// opções globais (arranque, árvore, grid, editor, log, tabs, export de grid e SQL, tema), o estado por tabela das
/// sessões importadas (<c>&lt;db&gt;|&lt;tabela&gt;</c>: Filter, Sort, HiddenColumns e RecentFilters, no mesmo formato) e o
/// histórico de queries (<c>QueryHistory</c>: <c>data|base|ms|sql</c>). Sem equivalente ficam de fora: cores do editor e
/// do grid por categoria, língua, pack de ícones, atualizações e estatísticas de uso.
/// </summary>
public sealed partial class HeidiSettingsImporter
{
    /// <summary>Estilos VCL que o HeidiSQL trata como escuros (anexo C §8).</summary>
    private static readonly HashSet<string> DarkStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Amakrits", "Aqua Graphite", "Auric", "Carbon", "Charcoal Dark Slate", "Cobalt XEMedia", "Glossy", "Glow", "Golden Graphite",
        "Material", "Onyx Blue", "Ruby Graphite", "TabletDark", "Windows10 Dark", "Windows10 SlateGray", "Windows10Dark", "Windows10SlateGray",
        "CharcoalDarkSlate",
    };

    /// <summary>Codificações do export de grid pela ordem do HeidiSQL (<c>GridExportEncoding</c> é o índice).</summary>
    private static readonly string?[] GridExportEncodings = ["ANSI", "ASCII", "Unicode", "Unicode Big Endian", "UTF-8", null, "UTF-8-BOM"];

    /// <summary>Opções globais (valores na raiz da chave do HeidiSQL); devolve quantas foram aplicadas.</summary>
    private int ImportPreferences(HeidiSettingsFile file)
    {
        var root = new Dictionary<string, HeidiValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in file.Values.Where(v => v.KeyPath.Count == 0))
        {
            root[value.Name] = value;
        }
        var count = 0;
        void Bool(string name, Action<bool> set)
        {
            if (root.TryGetValue(name, out var v))
            {
                set(v.AsBool());
                count++;
            }
        }
        void Int(string name, int min, int max, Action<int> set)
        {
            if (root.TryGetValue(name, out var v) && v.AsInt() is { } n)
            {
                set(Math.Clamp(n, min, max));
                count++;
            }
        }
        void Str(string name, Action<string> set, bool allowEmpty = false)
        {
            if (root.TryGetValue(name, out var v) && (allowEmpty || v.Value.Length > 0))
            {
                set(v.Value);
                count++;
            }
        }

        var s = settings;
        // Arranque e Session Manager.
        Bool("AutoReconnect", v => s.SessionManager.AutoReconnect = v);
        Bool("RestoreLastUsedDB", v => s.SessionManager.RestoreLastUsedDatabase = v);
        Int("SessionManager_ListWidth", 100, 2000, v => s.SessionManager.ListWidth = v);
        Int("SessionManager_WindowWidth", 400, 4000, v => s.SessionManager.WindowWidth = v);
        Int("SessionManager_WindowHeight", 300, 3000, v => s.SessionManager.WindowHeight = v);
        Bool("SessionManager_ListFoldersAtTop", v => s.SessionManager.FoldersAtTop = v);
        Bool("RestoreTabs", v => s.Query.RestoreTabs = v);
        // Árvore e painéis.
        Bool("GroupTreeObjects", v => s.Tree.GroupObjects = v);
        Bool("TreeShowColumns", v => s.Display.ColumnsInTree = v);
        Bool("DisplayObjectSizeColumn", v => s.Display.ObjectSizes = v);
        Bool("AutoExpand", v => s.Tree.AutoExpand = v);
        Bool("DoubleClickInsertsNodeText", v => s.Tree.DoubleClickInsertsNodeText = v);
        Bool("DisplayTreeFilters", v => s.Display.TreeFilters = v);
        Int("dbtreewidth", 40, 2000, v => s.Panels.TreeWidth = v);
        Bool("DisplayLogPanel", v => s.Display.LogPanel = v);
        Bool("FilterPanel", v => s.Display.FilterPanel = v);
        Bool("DisplayBars", v => s.Display.Bars = v);
        // Grid.
        Int("DatagridRowsPerStep", 1, 1_000_000, v => s.Data.RowsPerStep = v);
        Int("DatagridMaximumRows", 1, 100_000_000, v => s.Data.MaximumRows = v);
        Int("MaxColWidth", 20, 2000, v => s.Data.MaxColumnWidth = v);
        Str("DataFontName", v => s.Data.FontName = v);
        Int("DataFontSize", 6, 30, v => s.Data.FontSize = v);
        Bool("ForeignDropDown", v => s.Data.ForeignDropDown = v);
        Bool("DisplayBLOBsAsText", v => s.Data.BlobAsText = v);
        Bool("WarnUnsafeUpdates", v => s.Query.WarnUnsafeUpdates = v);
        // Editor SQL e execução.
        Str("FontName", v => s.Query.EditorFont = v);
        Int("FontSize", 6, 40, v => s.Query.EditorFontSize = v);
        Int("TabWidth", 1, 16, v => s.Query.TabWidth = v);
        Bool("TabsToSpaces", v => s.Query.TabsToSpaces = v);
        Bool("WrapLongLines", v => s.Query.WordWrap = v);
        Bool("CodeFolding", v => s.Query.CodeFolding = v);
        Bool("CompletionProposal", v => s.Query.CompletionEnabled = v);
        Int("CompletionProposalInterval", 0, 10_000, v => s.Query.CompletionDelayMs = v);
        Bool("AutoUppercase", v => s.Query.AutoUppercase = v);
        Str("Delimiter", v => s.Query.Delimiter = v);
        Int("MaxQueryResults", 1, 100, v => s.Query.MaxResults = v);
        Bool("StopOnErrorsInBatchMode", v => s.Query.StopOnErrors = v);
        Bool("SingleQueries", v => s.Query.BatchInOneGo = !v);
        Bool("QueryHistory", v => s.Query.HistoryEnabled = v);
        Bool("PromptSaveFileOnTabClose", v => s.Query.PromptSaveOnClose = v);
        Str("CustomSnippetsDirectory", v => s.Query.SnippetsDirectory = v);
        Bool("TabCloseOnDoubleClick", v => s.Tabs.CloseOnDoubleClick = v);
        Bool("TabCloseOnMiddleClick", v => s.Tabs.CloseOnMiddleClick = v);
        Bool("TabsInMultipleLines", v => s.Tabs.MultipleLines = v);
        // SQL log.
        Int("logsqlnum", 1, 1_000_000, v => s.Log.MaxLines = v);
        Int("logsqlwidth", 0, 1_000_000, v => s.Log.MaxLineLength = v);
        Bool("LogErrors", v => s.Log.Errors = v);
        Bool("LogUserSQL", v => s.Log.UserSql = v);
        Bool("LogSQL", v => s.Log.InternalSql = v);
        Bool("LogInfos", v => s.Log.Info = v);
        Bool("LogDebug", v => s.Log.Debug = v);
        Bool("LogScript", v => s.Log.Script = v);
        Bool("LogTimestamp", v => s.Log.Timestamps = v);
        Bool("LogHorizontalScrollbar", v => s.Log.HorizontalScrollbar = v);
        // Export de grid (ficheiro e clipboard) e SQL export.
        var e = s.Export;
        Bool("GridExportOutputCopy", v => e.OutputToClipboard = v);
        Str("GridExportFilename", v => e.FileName = v);
        Str("GridExportRecentFiles", v => e.RecentFiles = [.. v.Split('|', StringSplitOptions.RemoveEmptyEntries).Take(10)]);
        Int("GridExportFormat", 0, 15, v => e.Format = v);
        Int("GridExportEncoding", 0, GridExportEncodings.Length - 1, v => e.Encoding = GridExportEncodings[v] ?? "UTF-8");
        Int("GridExportSelection", 0, 1, v => e.SelectedRowsOnly = v == 0);
        Bool("GridExportColumnNames", v => e.ColumnNames = v);
        Bool("GridExportAutoInc", v => e.AutoIncrement = v);
        Bool("GridExportFocusedColumnOnly", v => e.FocusedColumnOnly = v);
        Bool("GridExportIncludeQuery", v => e.IncludeQuery = v);
        Bool("GridExportRemoveLinebreaks", v => e.RemoveLinebreaks = v);
        Bool("GridExportOpenFile", v => e.OpenFile = v);
        Str("GridExportSeparator", v => e.Separator = v, allowEmpty: true);
        Str("GridExportEncloser", v => e.Encloser = v, allowEmpty: true);
        Str("GridExportTerminator", v => e.Terminator = v, allowEmpty: true);
        Str("GridExportNull", v => e.NullValue = v, allowEmpty: true);
        Bool("GridExportClpColumnNames", v => e.ClipboardColumnNames = v);
        Bool("GridExportClpAutoInc", v => e.ClipboardAutoIncrement = v);
        Bool("GridExportClpRemoveLinebreaks", v => e.ClipboardRemoveLinebreaks = v);
        Str("GridExportClpSeparator", v => e.ClipboardSeparator = v, allowEmpty: true);
        Str("GridExportClpEncloser", v => e.ClipboardEncloser = v, allowEmpty: true);
        Str("GridExportClpTerminator", v => e.ClipboardTerminator = v, allowEmpty: true);
        Str("GridExportClpNull", v => e.ClipboardNullValue = v, allowEmpty: true);
        Bool("ExportSQL_CreateDatabases", v => s.SqlExport.CreateDatabases = v);
        Int("DataInsertSize", 0, 1_048_576, v => s.SqlExport.MaxInsertSizeKb = v);
        Bool("ExportSQLAddComments", v => s.SqlExport.AddComments = v);
        Bool("ExportSQLRemoveDefiner", v => s.SqlExport.RemoveDefiner = v);
        // Tema: os estilos escuros do HeidiSQL dão o tema escuro; os outros, o claro.
        Str("Theme", v => s.Theme = DarkStyles.Contains(v.Trim()) ? ThemeNames.Dark : ThemeNames.Light);
        return count;
    }

    /// <summary><c>LastSessions</c> (caminhos separados por <c>|</c>) e <c>LastActiveSession</c> para os ids do catálogo.</summary>
    private void ImportLastSessions(HeidiSettingsFile file)
    {
        string? Root(string name) => file.Values.FirstOrDefault(v => v.KeyPath.Count == 0 && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        if (Root("LastSessions") is { Length: > 0 } last)
        {
            var ids = last.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => catalog.FindByPath(path)?.Id)
                .OfType<Guid>()
                .Distinct()
                .ToList();
            if (ids.Count > 0)
            {
                settings.SessionManager.LastSessions = ids;
            }
        }
        if (Root("LastActiveSession") is { Length: > 0 } active && catalog.FindByPath(active) is { } session)
        {
            settings.SessionManager.LastActiveSession = session.Id;
        }
    }

    /// <summary>
    /// Estado por tabela (subchaves <c>&lt;db&gt;|&lt;tabela&gt;</c>, também <c>host|db|tabela</c>) e histórico de queries de
    /// uma sessão importada. Devolve (estados de tabela, entradas de histórico).
    /// </summary>
    private (int Tables, int History) ImportSessionState(SessionDefinition session, string sessionKey, Dictionary<string, Dictionary<string, HeidiValue>> keys)
    {
        if (appDatabase is null)
        {
            return (0, 0);
        }
        var tables = 0;
        var history = 0;
        var sessionId = session.Id.ToString("D");
        var prefix = sessionKey + "\\";
        foreach (var (key, values) in keys)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var rest = key[prefix.Length..].Split('\\');
            if (rest.Length == 1 && rest[0].Equals("QueryHistory", StringComparison.OrdinalIgnoreCase))
            {
                history += ImportHistory(session, values.Values);
                continue;
            }
            var parts = rest[0].Split('|');
            if (parts.Length < 2 || parts[^1].Length == 0 || parts[^2].Length == 0)
            {
                continue;
            }
            var table = new TableKey(sessionId, parts[^2], parts[^1]);
            if (rest.Length == 1)
            {
                var any = false;
                foreach (var name in new[] { "Filter", "Sort", "HiddenColumns" })
                {
                    if (values.TryGetValue(name, out var value) && value.Value.Length > 0)
                    {
                        appDatabase.SetTableState(table, name, value.Value);
                        any = true;
                    }
                }
                tables += any ? 1 : 0;
            }
            else if (rest.Length == 2 && rest[1].Equals("RecentFilters", StringComparison.OrdinalIgnoreCase))
            {
                // Valores 1…20, o 1 é o mais recente: entram do mais antigo para o mais recente.
                foreach (var filter in values.Values
                             .Select(v => (Order: int.TryParse(v.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue, v.Value))
                             .Where(v => v.Value.Length > 0)
                             .OrderByDescending(v => v.Order))
                {
                    appDatabase.AddRecentFilter(table, filter.Value);
                }
            }
        }
        return (tables, history);
    }

    /// <summary>
    /// Valores <c>data|base|duração_ms|sql</c> (o SQL pode ter <c>|</c>), do mais antigo para o mais recente. Entradas
    /// com passwords ficam de fora, como no histórico do Eunomia.
    /// </summary>
    private int ImportHistory(SessionDefinition session, IEnumerable<HeidiValue> values)
    {
        var entries = new List<(DateTimeOffset At, string? Database, TimeSpan Duration, string Sql)>();
        foreach (var value in values)
        {
            var parts = value.Value.Split('|', 4);
            if (parts.Length < 4 || parts[3].Trim().Length == 0 || Engine.Sql.SecretMasking.ContainsSecret(parts[3]))
            {
                continue;
            }
            var at = DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
                ? new DateTimeOffset(d)
                : DateTimeOffset.UnixEpoch;
            var ms = long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            entries.Add((at, parts[1].Length == 0 ? null : parts[1], TimeSpan.FromMilliseconds(ms), parts[3].Trim()));
        }
        foreach (var entry in entries.OrderBy(e => e.At))
        {
            appDatabase!.AddHistory(session.Name, entry.Database, entry.Sql, entry.At, entry.Duration);
        }
        return entries.Count;
    }
}
