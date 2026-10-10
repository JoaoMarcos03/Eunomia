using System.Globalization;
using Eunomia.App.Commands;
using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;

namespace Eunomia.Infra.Settings.HeidiImport;

/// <summary>Result of the import (shown to the user and in the SQL log).</summary>
public sealed record HeidiImportResult(
    IReadOnlyList<SessionDefinition> ImportedSessions,
    IReadOnlyList<string> SkippedSessions,
    int ImportedFolders,
    int ImportedShortcuts,
    int ImportedFavorites,
    int ImportedPasswords,
    IReadOnlyList<string> Warnings,
    int ImportedPreferences = 0,
    int ImportedTableStates = 0,
    int ImportedHistory = 0);

/// <summary>
/// Import of HeidiSQL's settings (FAM-S05, REQ-SES-02, TR-02): sessions with folders, colours, SSH and SSL, statistics,
/// favourites and <c>Shortcut1_*</c> shortcuts; and (phase 13) the preferences that have an equivalent, the per-table state
/// and the query history of the imported sessions. Add-only, as in HeidiSQL: sessions that already exist (same path) and
/// shortcuts already redefined are not replaced; HeidiSQL's preferences replace Eunomia's. De-obfuscated passwords go
/// straight to the secret vault (section 23.2); they never stay in the JSON.
/// </summary>
public sealed partial class HeidiSettingsImporter(SessionCatalog catalog, AppSettings settings, CommandRegistry commands, ISecretStore secrets, IAppDatabase? appDatabase)
{
    /// <summary>Delphi's <c>clNone</c> and <c>clDefault</c>: no session colour.</summary>
    private const int ClNone = 0x1FFFFFFF;
    private const int ClDefault = 0x20000000;

    public HeidiImportResult Import(HeidiSettingsFile file, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(file);
        var warnings = new List<string>();
        if (file.InvalidLines > 0)
        {
            warnings.Add($"{file.InvalidLines} invalid line(s) ignored.");
        }

        // Key tree under Servers.
        var keys = new Dictionary<string, Dictionary<string, HeidiValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in file.Values.Where(v => v.KeyPath.Count > 0 && v.KeyPath[0].Equals("Servers", StringComparison.OrdinalIgnoreCase)))
        {
            for (var depth = 2; depth <= value.KeyPath.Count; depth++)
            {
                var key = string.Join('\\', value.KeyPath.Skip(1).Take(depth - 1));
                if (!keys.ContainsKey(key))
                {
                    keys[key] = new Dictionary<string, HeidiValue>(StringComparer.OrdinalIgnoreCase);
                }
            }
            if (value.KeyPath.Count >= 2)
            {
                keys[string.Join('\\', value.KeyPath.Skip(1))][value.Name] = value;
            }
        }

        var imported = new List<SessionDefinition>();
        var skipped = new List<string>();
        var folders = 0;
        var passwords = 0;
        var favorites = 0;
        var tableStates = 0;
        var historyEntries = 0;

        void Walk(string prefix, List<string> folder)
        {
            var children = keys.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.Length > prefix.Length && !k[prefix.Length..].Contains('\\', StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var key in children)
            {
                var name = key[prefix.Length..];
                var values = keys[key];
                if (values.TryGetValue("Folder", out var isFolder) && isFolder.AsBool())
                {
                    List<string> path = [.. folder, name];
                    if (!catalog.FolderExists(path))
                    {
                        catalog.Folders.Add(path);
                        folders++;
                    }
                    Walk(key + "\\", path);
                    continue;
                }
                if (values.Count == 0)
                {
                    continue;
                }
                var sessionPath = folder.Count == 0 ? name : string.Join('\\', folder) + "\\" + name;
                if (catalog.FindByPath(sessionPath) is not null)
                {
                    skipped.Add(sessionPath);
                    continue;
                }
                var session = ToSession(name, folder, values, now, warnings);
                if (values.TryGetValue("Password", out var pw) && pw.Value.Length > 0)
                {
                    if (TryDecrypt(pw.Value, sessionPath, warnings) is { } plain)
                    {
                        secrets.Set(SecretKeys.Password(session.Id), plain);
                        session.PasswordSaved = true;
                        passwords++;
                    }
                }
                else if (!session.LoginPrompt)
                {
                    // In HeidiSQL, a missing password is an empty password (it does not ask for credentials).
                    secrets.Set(SecretKeys.Password(session.Id), string.Empty);
                    session.PasswordSaved = true;
                }
                if (values.TryGetValue("SSHtunnelPassword", out var sshPw) && sshPw.Value.Length > 0
                    && TryDecrypt(sshPw.Value, sessionPath, warnings) is { } sshPlain)
                {
                    secrets.Set(SecretKeys.SshPassword(session.Id), sshPlain);
                    passwords++;
                }
                if (values.TryGetValue("FavoriteObjects", out var fav) && appDatabase is not null)
                {
                    foreach (var line in fav.Value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        appDatabase.SetFavorite(session.Id.ToString("D"), line, true);
                        favorites++;
                    }
                }
                catalog.Sessions.Add(session);
                imported.Add(session);
                var (tables, history) = ImportSessionState(session, key, keys);
                tableStates += tables;
                historyEntries += history;
            }
        }

        Walk(string.Empty, []);
        var shortcuts = ImportShortcuts(file, warnings);
        var preferences = ImportPreferences(file);
        ImportLastSessions(file);
        return new HeidiImportResult(imported, skipped, folders, shortcuts, favorites, passwords, warnings, preferences, tableStates, historyEntries);
    }

    private static string? TryDecrypt(string encoded, string session, List<string> warnings)
    {
        try
        {
            return HeidiPassword.Decrypt(encoded);
        }
        catch (FormatException)
        {
            warnings.Add($"Could not read the password of session \"{session}\".");
            return null;
        }
    }

    private static SessionDefinition ToSession(string name, List<string> folder, Dictionary<string, HeidiValue> v, DateTimeOffset now, List<string> warnings)
    {
        string Str(string key, string fallback = "") => v.TryGetValue(key, out var x) ? x.Value : fallback;
        int Int(string key, int fallback) => v.TryGetValue(key, out var x) && (x.AsInt() ?? (int.TryParse(x.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null)) is { } i ? i : fallback;
        bool Bool(string key, bool fallback) => v.TryGetValue(key, out var x) ? x.AsBool() : fallback;

        var netTypeOrdinal = Int("NetType", 0);
        var netType = Enum.IsDefined(typeof(NetType), netTypeOrdinal) ? (NetType)netTypeOrdinal : NetType.MySqlTcpIp;
        if (!Enum.IsDefined(typeof(NetType), netTypeOrdinal))
        {
            warnings.Add($"Session \"{name}\": unknown network type {netTypeOrdinal}, using MariaDB or MySQL (TCP/IP).");
        }
        var session = new SessionDefinition
        {
            Name = CatalogName(name),
            Folder = [.. folder],
            NetType = netType,
            Library = Str("Library"),
            Host = Str("Host", "127.0.0.1"),
            Port = Int("Port", NetTypes.DefaultPort(netType)),
            User = Str("User", NetTypes.DefaultUser(netType)),
            LoginPrompt = Bool("LoginPrompt", false),
            WindowsAuth = Bool("WindowsAuth", false),
            Compressed = Bool("Compressed", false),
            Databases = Str("Databases"),
            Comment = Str("Comment"),
            StartupScript = Str("StartupScriptFilename"),
            QueryTimeout = Int("QueryTimeout", 30),
            KeepAlive = Int("KeepAlive", 20),
            LocalTimeZone = Bool("LocalTimeZone", false),
            FullTableStatus = Bool("FullTableStatus", true),
            CleartextAuth = Bool("CleartextPluginEnabled", false),
            ForceUnicode = Bool("ForceUnicode", true),
            Color = ToColor(Int("TreeBackground", ClNone)),
            HideDatabasePattern = Str("IgnoreDatabasePattern"),
            LogFilePath = Str("LogFilePath"),
            LogDdl = Bool("LogFileDdl", false),
            LogDml = Bool("LogFileDml", false),
            LastUsedDatabase = Str("lastUsedDB"),
        };
        session.Ssh = new SshSettings
        {
            Active = Bool("SSHtunnelActive", NetTypes.UsesSshByDefault(netType)),
            Executable = Str("SshExecutable"),
            Host = Str("SSHtunnelHost"),
            Port = Int("SSHtunnelHostPort", 22),
            User = Str("SSHtunnelUser"),
            PrivateKeyFile = Str("SSHtunnelPrivateKey"),
            Timeout = Math.Max(1, Int("SSHtunnelTimeout", 4)),
            LocalPort = Int("SSHtunnelPort", 0),
        };
        session.Ssl = new SslSettings
        {
            Active = Bool("SSL_Active", Str("SSL_Key").Length > 0),
            PrivateKey = Str("SSL_Key"),
            CaCertificate = Str("SSL_CA"),
            Certificate = Str("SSL_Cert"),
            Cipher = Str("SSL_Cipher"),
            Verification = Math.Clamp(Int("SSL_Verification", 2), 0, 2),
        };
        session.Statistics = new SessionStatistics
        {
            Created = ParseDate(Str("SessionCreated")) ?? now,
            LastConnect = ParseDate(Str("LastConnect")),
            ConnectCount = Int("ConnectCount", 0),
            RefusedCount = Int("RefusedCount", 0),
            ServerVersionFull = Str("ServerVersionFull"),
        };
        return session;
    }

    private static string CatalogName(string name) => SessionCatalog.NormalizeName(name).Length == 0 ? "Unnamed" : SessionCatalog.NormalizeName(name);

    /// <summary>Delphi TColor (<c>$00BBGGRR</c>) to 0xRRGGBB; <c>clNone</c>, <c>clDefault</c> and system colours = no colour.</summary>
    public static uint? ToColor(int tcolor)
    {
        if (tcolor is ClNone or ClDefault || tcolor < 0 || (tcolor & unchecked((int)0xFF000000)) != 0)
        {
            return null;
        }
        var r = (uint)tcolor & 0xFF;
        var g = ((uint)tcolor >> 8) & 0xFF;
        var b = ((uint)tcolor >> 16) & 0xFF;
        return (r << 16) | (g << 8) | b;
    }

    /// <summary>Dates saved as <c>yyyy-mm-dd hh:nn:ss</c> text (the format HeidiSQL forces), in local time.</summary>
    private static DateTimeOffset? ParseDate(string text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? new DateTimeOffset(d)
            : null;

    /// <summary><c>Shortcut1_&lt;action&gt;</c> shortcuts (TShortCut: Ctrl = $4000, Shift = $2000, Alt = $8000 + virtual key).</summary>
    private int ImportShortcuts(HeidiSettingsFile file, List<string> warnings)
    {
        var count = 0;
        foreach (var value in file.Values.Where(v => v.KeyPath.Count == 0 && v.Name.StartsWith("Shortcut1_", StringComparison.OrdinalIgnoreCase)))
        {
            var action = value.Name["Shortcut1_".Length..];
            if (commands.FindByHeidiSqlAction(action) is not { } command)
            {
                warnings.Add($"Shortcut for unknown action {action} ignored.");
                continue;
            }
            if (settings.Shortcuts.ContainsKey(command.Id) || value.AsInt() is not { } code)
            {
                continue;
            }
            var chord = DecodeShortcut(code);
            if (code != 0 && chord is null)
            {
                warnings.Add($"Shortcut {code} of {action} could not be read.");
                continue;
            }
            if (chord == command.DefaultShortcut)
            {
                continue;
            }
            settings.Shortcuts[command.Id] = chord?.ToString() ?? string.Empty;
            count++;
        }
        return count;
    }

    public static KeyChord? DecodeShortcut(int code)
    {
        if (code == 0)
        {
            return null;
        }
        var modifiers = ChordModifiers.None;
        if ((code & 0x4000) != 0)
        {
            modifiers |= ChordModifiers.Ctrl;
        }
        if ((code & 0x2000) != 0)
        {
            modifiers |= ChordModifiers.Shift;
        }
        if ((code & 0x8000) != 0)
        {
            modifiers |= ChordModifiers.Alt;
        }
        var vk = code & 0xFF;
        string? key = vk switch
        {
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F).ToString(CultureInfo.InvariantCulture),
            0x2D => "Ins",
            0x2E => "Del",
            0x1B => "Esc",
            0x0D => "Enter",
            0x22 => "PgDn",
            0x21 => "PgUp",
            0x20 => "Space",
            0x09 => "Tab",
            0x24 => "Home",
            0x23 => "End",
            0x08 => "Backspace",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            _ => null,
        };
        return key is null ? null : new KeyChord(modifiers, key);
    }
}
