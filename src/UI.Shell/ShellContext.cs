using System.Reflection;
using Eunomia.App.Commands;
using Eunomia.App.Settings;
using Eunomia.Presentation.Logging;
using Eunomia.Presentation.Shell;

namespace Eunomia.UI.Shell;

/// <summary>
/// Serviços partilhados pelo shell (composition root em <c>App.Desktop</c>): settings, registo de comandos com os
/// atalhos do utilizador, SQL log, status bar e <c>app.db</c>.
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

    /// <summary>Atalhos das settings que não foram aplicados (comando desconhecido ou texto inválido).</summary>
    public IReadOnlyList<string> IgnoredShortcuts { get; }

    public SqlLogBuffer Log { get; }

    /// <summary>Sessões gravadas, cofre e ligações (null nas medições e nos testes que não precisam).</summary>
    public ShellSessions? Sessions { get; init; }

    /// <summary>Pasta onde fica o código de objetos que não voltaram a ser criados depois de uma gravação falhada.</summary>
    public string? RecoveryDirectory { get; init; }

    /// <summary>Pasta dos ficheiros de spill dos resultados (secção 11.3); sem ela, os resultados ficam em memória.</summary>
    public string? SpillDirectory { get; init; }

    /// <summary>Journal das query tabs (restauro e crash, PR-21); sem ele, as tabs não são guardadas.</summary>
    public Eunomia.App.Query.ITabJournal? TabJournal { get; init; }

    /// <summary>Orçamento global da memória dos resultados (PR-17), partilhado pelo Data tab e pelas query tabs.</summary>
    public Eunomia.Engine.Results.ResultMemoryPool ResultPool { get; } = new();

    /// <summary>Log de diagnóstico em ficheiro (secção 21.5); sem ele, nada é registado fora do SQL log.</summary>
    public Eunomia.App.Diagnostics.IDiagnosticLog Diagnostics { get; init; } = Eunomia.App.Diagnostics.NullDiagnosticLog.Instance;

    /// <summary>Avisos do arranque para o SQL log (ficheiros de settings ou de sessões danificados e recuperados).</summary>
    public IReadOnlyList<string> StartupWarnings { get; init; } = [];

    /// <summary>Crash report da execução anterior, para o aviso no arranque.</summary>
    public string? PreviousCrashReport { get; init; }

    /// <summary>Journal dos jobs de import (retoma do Run SQL file); sem ele, um job parado não se retoma.</summary>
    public Eunomia.Engine.Transfer.TransferJournal? TransferJournal { get; init; }

    public StatusBarModel Status { get; } = new();

    public static string Version { get; } =
        typeof(ShellContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public void SaveSettings()
    {
        Settings.Shortcuts = Commands.ExportShortcutOverrides();
        Store.Save(Settings);
    }
}

/// <summary>Serviços de sessões do shell (Fase 3): catálogo, persistência, cofre e ligações.</summary>
public sealed class ShellSessions
{
    public required Eunomia.App.Sessions.SessionCatalog Catalog { get; init; }

    public required Eunomia.App.Sessions.ISessionStore Store { get; init; }

    public required Eunomia.App.Secrets.ISecretStore Secrets { get; init; }

    public required Eunomia.App.Sessions.SessionConnector Connector { get; init; }

    /// <summary>known_hosts do SSH (para gravar host keys confirmadas).</summary>
    public Eunomia.Drivers.Abstractions.ISshTunnelFactory? Ssh { get; init; }

    /// <summary>Mostrar o Session Manager ao abrir a janela (Cancel no arranque fecha a aplicação).</summary>
    public bool ShowManagerOnStartup { get; init; }
}
