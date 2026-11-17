using System.Globalization;
using System.Reflection;
using Eunomia.App.Commands;
using Eunomia.App.Settings;
using Eunomia.App.Updates;
using Eunomia.Presentation.Logging;
using Eunomia.Presentation.Shell;

namespace Eunomia.UI.Shell;

/// <summary>
/// Services shared by the shell (composition root in <c>App.Desktop</c>): settings, the command registry with the
/// user's shortcuts, SQL log, status bar and <c>app.db</c>.
/// </summary>
public sealed class ShellContext
{
    public ShellContext(AppSettings settings, ISettingsStore store, IAppDatabase? appDatabase = null, SqlLogBuffer? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        Store = store;
        AppDatabase = appDatabase;
        Commands = HeidiSqlCatalog.CreateRegistry();
        IgnoredShortcuts = Commands.ImportShortcutOverrides(settings.Shortcuts);
        Log = log ?? new SqlLogBuffer(settings.Log);
    }

    public AppSettings Settings { get; }

    public ISettingsStore Store { get; }

    public IAppDatabase? AppDatabase { get; }

    public CommandRegistry Commands { get; }

    /// <summary>Shortcuts from the settings that were not applied (unknown command or invalid text).</summary>
    public IReadOnlyList<string> IgnoredShortcuts { get; }

    public SqlLogBuffer Log { get; }

    /// <summary>Saved sessions, vault and connections (null in measurements and in tests that do not need them).</summary>
    public ShellSessions? Sessions { get; init; }

    /// <summary>Folder for the code of objects that were not created again after a failed save.</summary>
    public string? RecoveryDirectory { get; init; }

    /// <summary>Folder for the results' spill files (section 11.3); without it, results stay in memory.</summary>
    public string? SpillDirectory { get; init; }

    /// <summary>Query tab journal (restore and crash, PR-21); without it, tabs are not saved.</summary>
    public Eunomia.App.Query.ITabJournal? TabJournal { get; init; }

    /// <summary>Global memory budget for results (PR-17), shared by the Data tab and the query tabs.</summary>
    public Eunomia.Engine.Results.ResultMemoryPool ResultPool { get; } = new();

    /// <summary>Diagnostic log in a file (section 21.5); without it, nothing is logged outside the SQL log.</summary>
    public Eunomia.App.Diagnostics.IDiagnosticLog Diagnostics { get; init; } = Eunomia.App.Diagnostics.NullDiagnosticLog.Instance;

    /// <summary>Startup warnings for the SQL log (damaged and recovered settings or sessions files).</summary>
    public IReadOnlyList<string> StartupWarnings { get; init; } = [];

    /// <summary>Crash report from the previous run, for the warning at startup.</summary>
    public string? PreviousCrashReport { get; init; }

    /// <summary>Import job journal (Run SQL file resume); without it, a stopped job cannot be resumed.</summary>
    public Eunomia.Engine.Transfer.TransferJournal? TransferJournal { get; init; }

    /// <summary>
    /// Processors and memory of this computer, which lower the result memory budgets (<see cref="MachineProfile"/>).
    /// <see cref="MachineProfile.Unknown"/> (nothing lowered) unless the composition root detects it.
    /// </summary>
    public MachineProfile Machine { get; init; } = MachineProfile.Unknown;

    /// <summary>Memory budget of one result in bytes: the setting, lowered to this computer.</summary>
    public long ResultMemoryBytes => (long)Math.Max(16, Machine.ResultMemoryMb(Settings.Data)) * 1024 * 1024;

    /// <summary>Memory budget of all results in bytes: the setting, lowered to this computer.</summary>
    public long GlobalResultMemoryBytes => (long)Math.Max(64, Machine.GlobalResultMemoryMb(Settings.Data)) * 1024 * 1024;

    public StatusBarModel Status { get; } = new();

    /// <summary>The update check; <c>null</c> in tests and the benchmark mode, which never ask GitHub.</summary>
    public UpdateChecker? Updates { get; init; }

    public static string Version { get; } =
        typeof(ShellContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The CI run that built this executable (<c>EunomiaBuild</c> in Directory.Build.props); <c>null</c> in local builds.</summary>
    public static int? Build { get; } =
        int.TryParse(
            typeof(ShellContext).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "EunomiaBuild")?.Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var build)
            ? build
            : null;

    /// <summary>This build as the releases name it (the update check compares them); <c>null</c> if the version cannot be read.</summary>
    public static ReleaseVersion? Release { get; } = ReleaseVersion.TryParse(Version)?.WithBuild(Build);

    /// <summary>"1.0.0-beta.2 (build 15)": the version with the build, for About and the log.</summary>
    public static string DisplayVersion => Release?.Display ?? Version;

    public void SaveSettings()
    {
        Settings.Shortcuts = Commands.ExportShortcutOverrides();
        Store.Save(Settings);
    }
}

/// <summary>The shell's session services (phase 3): catalog, persistence, vault and connections.</summary>
public sealed class ShellSessions
{
    public required Eunomia.App.Sessions.SessionCatalog Catalog { get; init; }

    public required Eunomia.App.Sessions.ISessionStore Store { get; init; }

    public required Eunomia.App.Secrets.ISecretStore Secrets { get; init; }

    public required Eunomia.App.Sessions.SessionConnector Connector { get; init; }

    /// <summary>SSH known_hosts (to save confirmed host keys).</summary>
    public Eunomia.Drivers.Abstractions.ISshTunnelFactory? Ssh { get; init; }

    /// <summary>Show the Session Manager when the window opens (Cancel at startup closes the application).</summary>
    public bool ShowManagerOnStartup { get; init; }
}
