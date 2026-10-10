namespace Eunomia.App.Sessions;

/// <summary>
/// Tipos de rede do HeidiSQL (anexo A §2.1). O ordinal é o que o HeidiSQL grava e tem de ficar estável para o
/// import. No MVP só os MySQL/MariaDB (TCP, SSH, RDS) ligam; os outros existem para importar e mostrar.
/// </summary>
public enum NetType
{
    MySqlTcpIp = 0,
    MySqlNamedPipe = 1,
    MySqlSshTunnel = 2,
    MsSqlNamedPipe = 3,
    MsSqlTcpIp = 4,
    MsSqlSpx = 5,
    MsSqlVines = 6,
    MsSqlRpc = 7,
    PgSqlTcpIp = 8,
    PgSqlSshTunnel = 9,
    Sqlite = 10,
    ProxySqlAdmin = 11,
    InterbaseTcpIp = 12,
    InterbaseLocal = 13,
    FirebirdTcpIp = 14,
    FirebirdLocal = 15,
    MySqlRds = 16,
    SqliteEncrypted = 17,
}

public static class NetTypes
{
    /// <summary>Nome mostrado no Session Manager (HeidiSQL).</summary>
    public static string DisplayName(NetType type) => type switch
    {
        NetType.MySqlTcpIp => "MariaDB or MySQL (TCP/IP)",
        NetType.MySqlNamedPipe => "MariaDB or MySQL (named pipe)",
        NetType.MySqlSshTunnel => "MariaDB or MySQL (SSH tunnel)",
        NetType.MsSqlNamedPipe => "Microsoft SQL Server (named pipe)",
        NetType.MsSqlTcpIp => "Microsoft SQL Server (TCP/IP)",
        NetType.MsSqlSpx => "Microsoft SQL Server (SPX/IPX)",
        NetType.MsSqlVines => "Microsoft SQL Server (Banyan VINES)",
        NetType.MsSqlRpc => "Microsoft SQL Server (Windows RPC)",
        NetType.PgSqlTcpIp => "PostgreSQL (TCP/IP)",
        NetType.PgSqlSshTunnel => "PostgreSQL (SSH tunnel)",
        NetType.Sqlite => "SQLite",
        NetType.ProxySqlAdmin => "ProxySQL Admin (Experimental)",
        NetType.InterbaseTcpIp => "Interbase (TCP/IP, experimental)",
        NetType.InterbaseLocal => "Interbase (Local, experimental)",
        NetType.FirebirdTcpIp => "Firebird (TCP/IP, experimental)",
        NetType.FirebirdLocal => "Firebird (Local, experimental)",
        NetType.MySqlRds => "MySQL on RDS",
        NetType.SqliteEncrypted => "SQLite (Encrypted)",
        _ => type.ToString(),
    };

    public static bool IsMySqlFamily(NetType type) =>
        type is NetType.MySqlTcpIp or NetType.MySqlNamedPipe or NetType.MySqlSshTunnel or NetType.ProxySqlAdmin or NetType.MySqlRds;

    /// <summary>Tipos com que o MVP liga (secção 27.1; named pipe fica de fora).</summary>
    public static bool IsSupported(NetType type) => type is NetType.MySqlTcpIp or NetType.MySqlSshTunnel or NetType.MySqlRds;

    /// <summary>SSH ligado por omissão (anexo A §1.6).</summary>
    public static bool UsesSshByDefault(NetType type) => type is NetType.MySqlSshTunnel or NetType.MySqlRds or NetType.PgSqlSshTunnel;

    public static int DefaultPort(NetType type) => type switch
    {
        NetType.ProxySqlAdmin => 6032,
        NetType.MsSqlNamedPipe or NetType.MsSqlTcpIp or NetType.MsSqlSpx or NetType.MsSqlVines or NetType.MsSqlRpc => 0,
        NetType.PgSqlTcpIp or NetType.PgSqlSshTunnel => 5432,
        NetType.InterbaseTcpIp or NetType.InterbaseLocal or NetType.FirebirdTcpIp or NetType.FirebirdLocal => 3050,
        NetType.Sqlite or NetType.SqliteEncrypted => 0,
        _ => 3306,
    };

    public static string DefaultUser(NetType type) => type switch
    {
        NetType.MsSqlNamedPipe or NetType.MsSqlTcpIp or NetType.MsSqlSpx or NetType.MsSqlVines or NetType.MsSqlRpc => "sa",
        NetType.PgSqlTcpIp or NetType.PgSqlSshTunnel => "postgres",
        NetType.InterbaseTcpIp or NetType.InterbaseLocal or NetType.FirebirdTcpIp or NetType.FirebirdLocal => "sysdba",
        NetType.Sqlite or NetType.SqliteEncrypted => string.Empty,
        _ => "root",
    };
}

/// <summary>Túnel SSH da sessão (tab "SSH tunnel"). A password vai para o cofre de segredos.</summary>
public sealed class SshSettings
{
    public bool Active { get; set; }

    /// <summary>plink.exe/ssh.exe: só usado no modo externo (D7), por escolha explícita.</summary>
    public string Executable { get; set; } = string.Empty;

    /// <summary>Vazio = host da base de dados.</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string User { get; set; } = string.Empty;

    public string PrivateKeyFile { get; set; } = string.Empty;

    /// <summary>Timeout de ligação SSH em segundos (HeidiSQL: 4, mínimo 1).</summary>
    public int Timeout { get; set; } = 4;

    /// <summary>Porta local do forward; 0 = escolhida pelo sistema.</summary>
    public int LocalPort { get; set; }

    /// <summary>Autenticar pelo agent (OpenSSH ou Pageant) antes de chave e password.</summary>
    public bool UseAgent { get; set; } = true;
}

/// <summary>Tab "SSL" (anexo A §1.6). Verificação: 0 nenhuma, 1 CA, 2 CA + hostname (omissão).</summary>
public sealed class SslSettings
{
    public bool Active { get; set; }

    public string PrivateKey { get; set; } = string.Empty;

    public string CaCertificate { get; set; } = string.Empty;

    public string Certificate { get; set; } = string.Empty;

    public string Cipher { get; set; } = string.Empty;

    public int Verification { get; set; } = 2;
}

/// <summary>Contadores do tab "Statistics" (FAM-S08) e a versão vista na última ligação.</summary>
public sealed class SessionStatistics
{
    public DateTimeOffset? Created { get; set; }

    public DateTimeOffset? LastConnect { get; set; }

    public int ConnectCount { get; set; }

    public int RefusedCount { get; set; }

    /// <summary>Versão completa do servidor na última ligação (coluna Version e ícone por vendor).</summary>
    public string ServerVersionFull { get; set; } = string.Empty;
}

/// <summary>
/// Uma sessão gravada (tabs Settings, SSH tunnel, Advanced, SSL e Statistics do Session Manager; anexo A §1.6).
/// As passwords nunca estão aqui: ficam no Credential Manager com a chave <see cref="Id"/> (secção 23.2).
/// </summary>
public sealed class SessionDefinition
{
    /// <summary>Identificador estável (alvo das credenciais); não muda com rename nem move.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Unnamed";

    /// <summary>Caminho de folders, do mais exterior para o mais interior (vazio = raiz).</summary>
    public List<string> Folder { get; set; } = [];

    public NetType NetType { get; set; } = NetType.MySqlTcpIp;

    /// <summary>Library do HeidiSQL (DLL). Guardada só para o export; o driver é gerido.</summary>
    public string Library { get; set; } = string.Empty;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 3306;

    public string User { get; set; } = "root";

    /// <summary>Há password gravada no cofre. Sem ela, a ligação pede-a (como "Prompt for credentials").</summary>
    public bool PasswordSaved { get; set; }

    public bool LoginPrompt { get; set; }

    public bool WindowsAuth { get; set; }

    public bool Compressed { get; set; }

    /// <summary>Whitelist separada por <c>;</c> (FAM-S04): substitui o <c>SHOW DATABASES</c>.</summary>
    public string Databases { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public SshSettings Ssh { get; set; } = new();

    public SslSettings Ssl { get; set; } = new();

    // Advanced.
    public string StartupScript { get; set; } = string.Empty;

    /// <summary>Timeout de execução no cliente em segundos (D8); 0 = sem limite.</summary>
    public int QueryTimeout { get; set; } = 30;

    /// <summary>Ping every X seconds (keep-alive); 0 desliga.</summary>
    public int KeepAlive { get; set; } = 20;

    public bool LocalTimeZone { get; set; }

    public bool FullTableStatus { get; set; } = true;

    public bool CleartextAuth { get; set; }

    public bool ForceUnicode { get; set; } = true;

    /// <summary>Cor da sessão 0xRRGGBB, ou <c>null</c> (FAM-S02).</summary>
    public uint? Color { get; set; }

    /// <summary>Hide database pattern (regex, FAM-S04).</summary>
    public string HideDatabasePattern { get; set; } = string.Empty;

    public string LogFilePath { get; set; } = string.Empty;

    public bool LogDdl { get; set; }

    public bool LogDml { get; set; }

    /// <summary>
    /// Modo só de leitura (secção 23.2, novo): o cliente só envia leituras (<c>ReadOnlyPolicy</c>) e o servidor fica em
    /// <c>SET SESSION TRANSACTION READ ONLY</c>; o grid não edita.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Perguntar antes de correr instruções que mudam dados ou estrutura nas query tabs, no Run SQL file e no import de
    /// CSV (Fase 14; para sessões de produção que não podem ser só de leitura).
    /// </summary>
    public bool ConfirmChanges { get; set; }

    /// <summary>Ligação de metadata separada (12.2); desligar volta ao modo de ligação única do HeidiSQL.</summary>
    public bool MetadataConnection { get; set; } = true;

    /// <summary>Ligações de job em simultâneo (12.6).</summary>
    public int JobConnections { get; set; } = 2;

    /// <summary>Última base usada (RestoreLastUsedDB).</summary>
    public string LastUsedDatabase { get; set; } = string.Empty;

    public SessionStatistics Statistics { get; set; } = new();

    /// <summary>Caminho como o HeidiSQL o mostra: <c>Folder\Sub\Sessão</c>.</summary>
    public string Path => Folder.Count == 0 ? Name : string.Join('\\', Folder) + "\\" + Name;

    public SessionDefinition Clone()
    {
        var copy = (SessionDefinition)MemberwiseClone();
        copy.Folder = [.. Folder];
        copy.Ssh = (SshSettings)CloneObject(Ssh);
        copy.Ssl = (SslSettings)CloneObject(Ssl);
        copy.Statistics = (SessionStatistics)CloneObject(Statistics);
        return copy;
    }

    private static object CloneObject(object value) => value switch
    {
        SshSettings s => new SshSettings
        {
            Active = s.Active, Executable = s.Executable, Host = s.Host, Port = s.Port, User = s.User,
            PrivateKeyFile = s.PrivateKeyFile, Timeout = s.Timeout, LocalPort = s.LocalPort, UseAgent = s.UseAgent,
        },
        SslSettings s => new SslSettings
        {
            Active = s.Active, PrivateKey = s.PrivateKey, CaCertificate = s.CaCertificate, Certificate = s.Certificate,
            Cipher = s.Cipher, Verification = s.Verification,
        },
        SessionStatistics s => new SessionStatistics
        {
            Created = s.Created, LastConnect = s.LastConnect, ConnectCount = s.ConnectCount, RefusedCount = s.RefusedCount,
            ServerVersionFull = s.ServerVersionFull,
        },
        _ => throw new ArgumentException("Tipo inesperado.", nameof(value)),
    };

    /// <summary>
    /// Os campos editáveis do Session Manager são iguais (sem id, nome, folder, password e estatísticas): decide o
    /// "Save modifications?" (anexo A §1.5).
    /// </summary>
    public bool HasSameSettings(SessionDefinition other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return NetType == other.NetType && Library == other.Library && Host == other.Host && Port == other.Port && User == other.User
            && LoginPrompt == other.LoginPrompt && WindowsAuth == other.WindowsAuth && Compressed == other.Compressed
            && Databases == other.Databases && Comment == other.Comment && StartupScript == other.StartupScript
            && QueryTimeout == other.QueryTimeout && KeepAlive == other.KeepAlive && LocalTimeZone == other.LocalTimeZone
            && FullTableStatus == other.FullTableStatus && CleartextAuth == other.CleartextAuth && ForceUnicode == other.ForceUnicode
            && Color == other.Color && HideDatabasePattern == other.HideDatabasePattern && LogFilePath == other.LogFilePath
            && LogDdl == other.LogDdl && LogDml == other.LogDml && ReadOnly == other.ReadOnly && ConfirmChanges == other.ConfirmChanges
            && MetadataConnection == other.MetadataConnection && JobConnections == other.JobConnections
            && Ssh.Active == other.Ssh.Active && Ssh.Executable == other.Ssh.Executable && Ssh.Host == other.Ssh.Host
            && Ssh.Port == other.Ssh.Port && Ssh.User == other.Ssh.User && Ssh.PrivateKeyFile == other.Ssh.PrivateKeyFile
            && Ssh.Timeout == other.Ssh.Timeout && Ssh.LocalPort == other.Ssh.LocalPort && Ssh.UseAgent == other.Ssh.UseAgent
            && Ssl.Active == other.Ssl.Active && Ssl.PrivateKey == other.Ssl.PrivateKey && Ssl.CaCertificate == other.Ssl.CaCertificate
            && Ssl.Certificate == other.Ssl.Certificate && Ssl.Cipher == other.Ssl.Cipher && Ssl.Verification == other.Ssl.Verification;
    }

    /// <summary>Copia os campos editáveis de <paramref name="source"/> (Save).</summary>
    public void CopySettingsFrom(SessionDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = source.Clone();
        NetType = copy.NetType;
        Library = copy.Library;
        Host = copy.Host;
        Port = copy.Port;
        User = copy.User;
        LoginPrompt = copy.LoginPrompt;
        WindowsAuth = copy.WindowsAuth;
        Compressed = copy.Compressed;
        Databases = copy.Databases;
        Comment = copy.Comment;
        StartupScript = copy.StartupScript;
        QueryTimeout = copy.QueryTimeout;
        KeepAlive = copy.KeepAlive;
        LocalTimeZone = copy.LocalTimeZone;
        FullTableStatus = copy.FullTableStatus;
        CleartextAuth = copy.CleartextAuth;
        ForceUnicode = copy.ForceUnicode;
        Color = copy.Color;
        HideDatabasePattern = copy.HideDatabasePattern;
        LogFilePath = copy.LogFilePath;
        LogDdl = copy.LogDdl;
        LogDml = copy.LogDml;
        ReadOnly = copy.ReadOnly;
        ConfirmChanges = copy.ConfirmChanges;
        MetadataConnection = copy.MetadataConnection;
        JobConnections = copy.JobConnections;
        Ssh = copy.Ssh;
        Ssl = copy.Ssl;
    }

    /// <summary>Muda o tipo de rede e repõe os defaults da família nos campos que o utilizador não mexeu.</summary>
    public void ChangeNetType(NetType type)
    {
        var old = NetType;
        if (Port == NetTypes.DefaultPort(old))
        {
            Port = NetTypes.DefaultPort(type);
        }
        if (User == NetTypes.DefaultUser(old))
        {
            User = NetTypes.DefaultUser(type);
        }
        if (Ssh.Active == NetTypes.UsesSshByDefault(old))
        {
            Ssh.Active = NetTypes.UsesSshByDefault(type);
        }
        NetType = type;
    }
}
