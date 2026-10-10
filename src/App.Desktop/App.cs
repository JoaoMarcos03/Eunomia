using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Eunomia.App.Commands;
using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;
using Eunomia.Drivers.MySql;
using Eunomia.Infra.Secrets.Windows;
using Eunomia.Infra.Settings;
using Eunomia.Infra.Settings.HeidiImport;
using Eunomia.Infra.Ssh;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Shell;
using Eunomia.UI.Shell.Views;

namespace Eunomia.App.Desktop;

/// <summary>Composition root (section 22): wires settings, sessions, vault, SSH, driver, <c>app.db</c> and the main window.</summary>
public sealed class App : Application
{
    private SqliteAppDatabase? _appDatabase;

    public override void Initialize()
    {
        StartupTrace.Mark("app-initialize");
        // Name in the macOS application menu ("About Eunomia", "Quit Eunomia") and in window managers.
        Name = "Eunomia";
        // Compact Fluent with the theme colours and the global styles (the same as the snapshots).
        ShellTheme.Install(this);
        DarkTitleBar.Install();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = BenchOptions.Parse(desktop.Args ?? []);
            ShellContext context;
            Func<string, string>? import = null;
            Func<string>? importRegistry = null;
            Func<string, string>? export = null;
            if (options.IsBench)
            {
                // Measurements with the spike's fixed dimensions and without touching the user's settings.
                var settings = new AppSettings();
                settings.MainWindow.Width = 1280;
                settings.MainWindow.Height = 800;
                var log = new SqlLogBuffer(settings.Log);
                context = new ShellContext(settings, new MemorySettingsStore(settings), null, log)
                {
                    // Only --connect-probe connects to sessions (in memory); the other measurements use synthetic data.
                    Sessions = options.ConnectProbe is null ? null : new ShellSessions
                    {
                        Catalog = new SessionCatalog(),
                        Store = new MemorySessionStore(),
                        Secrets = new MemorySecretStore(),
                        Connector = new SessionConnector(new MySqlProvider(), new MemorySecretStore(), null, new SessionLogAdapter(log)),
                    },
                };
            }
            else
            {
                var paths = AppDataPaths.Resolve(AppContext.BaseDirectory);
                paths.EnsureCreated();
                var diagnostics = new Eunomia.Infra.Observability.JsonLinesDiagnosticLog(paths.Logs);
                var previousCrash = Eunomia.Infra.Observability.CrashReports.TakeUnseen(paths.Logs);
                CrashHandling.Install(diagnostics, paths.Logs, ShellContext.Version, () => desktop.MainWindow);
                desktop.Exit += (_, _) => diagnostics.Dispose();
                var store = new JsonSettingsStore(paths.SettingsFile);
                _appDatabase = new SqliteAppDatabase(paths.AppDatabase);
                var settings = store.Load();
                var sessionStore = new JsonSessionStore(paths.SessionsFile);
                var catalog = sessionStore.Load();
                var warnings = new List<string>();
                if (store.LastCorruptBackup is { } badSettings)
                {
                    warnings.Add($"The settings file was damaged and was moved to {badSettings}; the previous copy (or the defaults) is in use.");
                }
                if (_appDatabase.CorruptBackup is { } badDatabase)
                {
                    warnings.Add($"app.db was damaged and was moved to {badDatabase}; favorites, recent filters and the query history start empty.");
                }
                if (sessionStore.LastCorruptBackup is { } badSessions)
                {
                    warnings.Add(sessionStore.RestoredFromBackup
                        ? $"The sessions file was damaged and was moved to {badSessions}; the sessions were restored from the previous copy."
                        : $"The sessions file was damaged and was moved to {badSessions}; no previous copy could be read.");
                }
                var secrets = new ResilientSecretStore(SystemSecretStore(paths), message => warnings.Add(message));
                if (!secrets.IsPersistent)
                {
                    warnings.Add(OperatingSystem.IsLinux()
                        ? "No system keyring (Secret Service) was found: passwords are kept only until Eunomia closes. Install GNOME Keyring or KWallet to save them."
                        : "No system keyring is available: passwords are kept only until Eunomia closes.");
                }
                var ssh = new SshTunnelFactory(new KnownHostsStore(Path.Combine(paths.Roaming, "known_hosts")));
                var log = new SqlLogBuffer(settings.Log);
                var connector = new SessionConnector(new MySqlProvider(), secrets, ssh, new SessionLogAdapter(log));
                // Spill files from earlier runs that closed badly (the files are DeleteOnClose; this catches the rest).
                _ = Task.Run(() => Eunomia.Engine.Results.SpillFile.CleanOrphans(paths.Spill));
                context = new ShellContext(settings, store, _appDatabase, log)
                {
                    SpillDirectory = paths.Spill,
                    StartupWarnings = warnings,
                    RecoveryDirectory = paths.Recovery,
                    Diagnostics = diagnostics,
                    PreviousCrashReport = previousCrash,
                    TabJournal = settings.Query.RestoreTabs ? new Eunomia.Infra.Settings.Journal.TabJournal(paths.TabsJournal) : null,
                    TransferJournal = new Eunomia.Engine.Transfer.TransferJournal(paths.JobsJournal),
                    Sessions = new ShellSessions
                    {
                        Catalog = catalog,
                        Store = sessionStore,
                        Secrets = secrets,
                        Connector = connector,
                        Ssh = ssh,
                        ShowManagerOnStartup = true,
                    },
                };
                import = path => ImportSettings(HeidiSettingsFile.Load(path), catalog, sessionStore, context, secrets);
                if (OperatingSystem.IsWindows() && HeidiRegistry.HasSessions())
                {
                    importRegistry = () => ImportSettings(
                        (OperatingSystem.IsWindows() ? HeidiRegistry.Read() : null) ?? new HeidiSettingsFile(), catalog, sessionStore, context, secrets);
                }
                export = path => ExportSettings(path, catalog, settings);
                desktop.Exit += (_, _) => _appDatabase.Dispose();
            }
            RequestedThemeVariant = PreferencesWindow.ThemeOf(options.Theme ?? context.Settings.Theme);
            StartupTrace.Mark("framework-ready");
            ShellTrace.Mark = StartupTrace.Mark;
            var window = new MainWindow(context)
            {
                ImportSettingsFile = import,
                ImportHeidiSqlRegistry = importRegistry,
                ExportSettingsFile = export,
                StartupRequest = CommandLine.Parse(desktop.Args ?? []),
            };
            if (options.Scale != 1.0)
            {
                window.ApplyContentScale(options.Scale);
            }
            StartupTrace.Mark("window-created");
            BenchHooks.Attach(window, options);
            desktop.MainWindow = window;
            if (OperatingSystem.IsMacOS())
            {
                // macOS application menu: About opens our dialog (the rest of the menu is the window's).
                var about = new NativeMenuItem("About Eunomia");
                about.Click += (_, _) => _ = Eunomia.UI.Shell.Views.AboutDialog.ShowAsync(window, ShellContext.Version);
                NativeMenu.SetMenu(this, new NativeMenu { Items = { about } });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// System vault (section 23.2; phase 14): Credential Manager and DPAPI on Windows, Keychain on macOS, Secret Service
    /// (libsecret) on Linux; <c>null</c> if there is none (secrets stay only in memory).
    /// </summary>
    private static ISecretStore? SystemSecretStore(AppDataPaths paths)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSecretStore("Eunomia/", paths.Secrets);
        }
        if (OperatingSystem.IsMacOS())
        {
            return new Eunomia.Infra.Secrets.MacOS.MacKeychainSecretStore();
        }
        if (OperatingSystem.IsLinux())
        {
            var store = new Eunomia.Infra.Secrets.Linux.SecretServiceStore();
            return store.IsAvailable ? store : null;
        }
        return null;
    }

    /// <summary>
    /// File › Import settings file (HeidiSQL file, <c>&lt;|||&gt;</c>) and Import sessions from HeidiSQL (registry):
    /// add-only (FAM-S05).
    /// </summary>
    private string ImportSettings(HeidiSettingsFile file, SessionCatalog catalog, ISessionStore store, ShellContext context, ISecretStore secrets)
    {
        var importer = new HeidiSettingsImporter(catalog, context.Settings, HeidiSqlCatalog.CreateRegistry(), secrets, _appDatabase);
        var result = importer.Import(file, DateTimeOffset.UtcNow);
        store.Save(catalog);
        context.SaveSettings();
        var lines = new List<string>
        {
            $"{result.ImportedSessions.Count} session(s) and {result.ImportedFolders} folder(s) imported, {result.SkippedSessions.Count} already existing session(s) left unchanged.",
            $"{result.ImportedPasswords} password(s) moved to the {KeyringName}, {result.ImportedShortcuts} shortcut(s), {result.ImportedFavorites} favorite(s).",
            $"{result.ImportedPreferences} preference(s), saved filters/sorting/columns of {result.ImportedTableStates} table(s), {result.ImportedHistory} query history entr(ies).",
        };
        if (result.ImportedPasswords > 0)
        {
            lines.Add("The imported file contains passwords in a reversible form: consider deleting it.");
        }
        lines.AddRange(result.Warnings);
        return string.Join('\n', lines);
    }

    private static string KeyringName =>
        OperatingSystem.IsWindows() ? "Windows Credential Manager" : OperatingSystem.IsMacOS() ? "macOS Keychain" : "system keyring";

    /// <summary>File › Export settings file: settings and sessions as JSON, without secrets (section 23.2).</summary>
    private static string ExportSettings(string path, SessionCatalog catalog, AppSettings settings)
    {
        var temp = Path.Combine(Path.GetTempPath(), "eunomia-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            new JsonSettingsStore(Path.Combine(temp, "settings.json")).Save(settings);
            new JsonSessionStore(Path.Combine(temp, "sessions.json")).Save(catalog);
            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("format", "eunomia-settings");
            writer.WriteNumber("version", 1);
            writer.WritePropertyName("settings");
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(temp, "settings.json"))).WriteTo(writer);
            writer.WritePropertyName("sessions");
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(temp, "sessions.json"))).WriteTo(writer);
            writer.WriteEndObject();
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
        return $"{catalog.Sessions.Count} session(s) exported without passwords.";
    }

    /// <summary>In-memory sessions (measurement mode).</summary>
    private sealed class MemorySessionStore : ISessionStore
    {
        public SessionCatalog Load() => new();

        public void Save(SessionCatalog catalog)
        {
        }
    }

    /// <summary>In-memory settings (measurement mode).</summary>
    private sealed class MemorySettingsStore(AppSettings settings) : ISettingsStore
    {
        public AppSettings Load() => settings;

        public void Save(AppSettings value)
        {
        }
    }
}
