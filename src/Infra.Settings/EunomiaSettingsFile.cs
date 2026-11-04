using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;

namespace Eunomia.Infra.Settings;

/// <summary>Result of importing an Eunomia settings file (shown to the user and in the SQL log).</summary>
public sealed record EunomiaImportResult(
    IReadOnlyList<SessionDefinition> ImportedSessions,
    IReadOnlyList<string> SkippedSessions,
    int ImportedFolders,
    int ImportedFavorites,
    bool ImportedPreferences);

/// <summary>
/// File › Export settings file and Import settings file in Eunomia's own format (section 23.2): one JSON document with
/// <c>"format": "eunomia-settings"</c>, the version, the settings, the sessions with their folders and the favourites of
/// each session. Never any secret: an imported session asks for its password on the first connection.
/// <para>
/// Import works like the HeidiSQL import (FAM-S05): sessions and folders are add-only (a session whose path already
/// exists is left as it is; a clashing id gets a new one), and the preferences of the file replace the current ones,
/// except what only makes sense on the machine that wrote it (window position, recent files and directories, last
/// sessions).
/// </para>
/// </summary>
public static class EunomiaSettingsFile
{
    public const string Format = "eunomia-settings";

    /// <summary>Version 1; the <c>favorites</c> object was added later and is optional when reading.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// The file looks like an Eunomia export (a JSON object) rather than a HeidiSQL settings file (text lines with
    /// <c>&lt;|||&gt;</c>). Only the first bytes are read.
    /// </summary>
    public static bool LooksLikeEunomiaFile(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[256];
        var read = stream.Read(head);
        var text = head[..read];
        if (text.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            text = text[3..];
        }
        foreach (var b in text)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }
            return b == (byte)'{';
        }
        return false;
    }

    /// <summary>Writes the export atomically; returns the summary for the user.</summary>
    public static string Export(string path, SessionCatalog catalog, AppSettings settings, IAppDatabase? appDatabase)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        var favorites = new JsonObject();
        var favoriteCount = 0;
        if (appDatabase is not null)
        {
            foreach (var session in catalog.Sessions)
            {
                var paths = appDatabase.GetFavorites(session.Id.ToString("D"));
                if (paths.Count > 0)
                {
                    favorites[session.Id.ToString("D")] = new JsonArray([.. paths.Select(p => JsonValue.Create(p))]);
                    favoriteCount += paths.Count;
                }
            }
        }
        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = CurrentVersion,
            ["settings"] = JsonSerializer.SerializeToNode(settings, SettingsJsonContext.Default.AppSettings),
            ["sessions"] = JsonSerializer.SerializeToNode(catalog, SessionsJsonContext.Default.SessionCatalog),
            ["favorites"] = favorites,
        };
        AtomicFile.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return $"{catalog.Sessions.Count} session(s), {catalog.AllFolders().Count} folder(s), {favoriteCount} favorite(s) and the preferences exported. Passwords are not exported.";
    }

    /// <summary>Imports a file written by <see cref="Export"/>.</summary>
    /// <exception cref="FormatException">Not an Eunomia settings file, or one from a newer version.</exception>
    public static EunomiaImportResult Import(string path, SessionCatalog catalog, AppSettings settings, IAppDatabase? appDatabase)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        JsonObject root;
        try
        {
            // A file edited by hand may have gained a UTF-8 BOM, which the JSON reader does not skip in a byte span.
            var bytes = File.ReadAllBytes(path).AsSpan();
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                bytes = bytes[3..];
            }
            root = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject()
                ?? throw new FormatException("The file is empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new FormatException("The file is neither an Eunomia nor a HeidiSQL settings file: " + ex.Message, ex);
        }
        if (root["format"]?.GetValueKind() != JsonValueKind.String || root["format"]!.GetValue<string>() != Format)
        {
            throw new FormatException("The file is neither an Eunomia nor a HeidiSQL settings file.");
        }
        if (root["version"] is { } version && version.GetValueKind() == JsonValueKind.Number && version.GetValue<int>() > CurrentVersion)
        {
            throw new FormatException("The settings file was written by a newer version of Eunomia.");
        }

        var preferences = false;
        if (root["settings"] is JsonObject settingsNode)
        {
            var imported = Deserialize(settingsNode, SettingsJsonContext.Default.AppSettings);
            ApplyPreferences(settings, SettingsMigrations.Upgrade(imported));
            preferences = true;
        }

        var sessions = new List<SessionDefinition>();
        var skipped = new List<string>();
        var folders = 0;
        var favorites = 0;
        if (root["sessions"] is JsonObject sessionsNode
            && JsonSessionStore.TryParse(Encoding.UTF8.GetBytes(sessionsNode.ToJsonString())) is { } file)
        {
            foreach (var folder in file.Folders.Where(f => f.Count > 0 && !catalog.FolderExists(f)))
            {
                catalog.Folders.Add([.. folder]);
                folders++;
            }
            var ids = new Dictionary<Guid, Guid>();
            foreach (var session in file.Sessions)
            {
                var sessionPath = string.Join('\\', session.Folder.Append(session.Name));
                if (catalog.FindByPath(sessionPath) is not null)
                {
                    skipped.Add(sessionPath);
                    continue;
                }
                var original = session.Id;
                if (catalog.Find(session.Id) is not null)
                {
                    session.Id = Guid.NewGuid();
                }
                // The file has no secrets: the first connection asks for the password.
                session.PasswordSaved = false;
                catalog.Sessions.Add(session);
                sessions.Add(session);
                ids[original] = session.Id;
            }
            if (appDatabase is not null && root["favorites"] is JsonObject favoritesNode)
            {
                foreach (var (id, list) in favoritesNode)
                {
                    if (!Guid.TryParse(id, out var original) || !ids.TryGetValue(original, out var target) || list is not JsonArray paths)
                    {
                        continue;
                    }
                    foreach (var item in paths)
                    {
                        if (item?.GetValueKind() == JsonValueKind.String)
                        {
                            appDatabase.SetFavorite(target.ToString("D"), item.GetValue<string>(), true);
                            favorites++;
                        }
                    }
                }
            }
        }
        return new EunomiaImportResult(sessions, skipped, folders, favorites, preferences);
    }

    private static T Deserialize<T>(JsonObject node, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class, new()
    {
        try
        {
            return node.Deserialize(type) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new FormatException("The settings in the file are not valid: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// The preferences of the file replace the current ones; what belongs to this machine stays (window geometry,
    /// recent files and directories, open and last sessions, the HeidiSQL import question).
    /// </summary>
    private static void ApplyPreferences(AppSettings target, AppSettings source)
    {
        target.Panels = source.Panels;
        target.Display = source.Display;
        target.Theme = source.Theme;
        target.Shortcuts = source.Shortcuts;
        target.Log = source.Log;
        target.Tabs = source.Tabs;
        target.Tree = source.Tree ?? new TreeOptions();
        target.Dialogs = source.Dialogs ?? new DialogOptions();
        target.Export = source.Export ?? new ExportOptions();
        target.Data = source.Data;
        target.Lists = source.Lists ?? [];

        source.Query.RecentFiles = target.Query.RecentFiles;
        target.Query = source.Query;

        var manager = source.SessionManager;
        manager.LastActiveSession = target.SessionManager.LastActiveSession;
        manager.LastSessions = target.SessionManager.LastSessions;
        manager.HeidiImportOffered = target.SessionManager.HeidiImportOffered;
        target.SessionManager = manager;

        var search = source.Search;
        search.FindHistory = target.Search.FindHistory;
        search.ReplaceHistory = target.Search.ReplaceHistory;
        target.Search = search;

        var sqlExport = source.SqlExport;
        sqlExport.Filenames = target.SqlExport.Filenames;
        sqlExport.ZipFilenames = target.SqlExport.ZipFilenames;
        sqlExport.Directories = target.SqlExport.Directories;
        target.SqlExport = sqlExport;

        var csvImport = source.CsvImport;
        csvImport.FileName = target.CsvImport.FileName;
        target.CsvImport = csvImport;
    }
}
