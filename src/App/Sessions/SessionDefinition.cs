namespace Eunomia.App.Sessions;

/// <summary>
/// HeidiSQL network types (appendix A §2.1). The ordinal is what HeidiSQL saves and must stay stable for the
/// import. In the MVP only the MySQL/MariaDB ones (TCP, SSH, RDS) connect; the others exist to import and display.
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
    /// <summary>Name shown in the Session Manager (HeidiSQL).</summary>
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

    /// <summary>Types the MVP connects with (section 27.1; named pipe is left out).</summary>
    public static bool IsSupported(NetType type) => type is NetType.MySqlTcpIp or NetType.MySqlSshTunnel or NetType.MySqlRds;

    /// <summary>SSH on by default (appendix A §1.6).</summary>
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

/// <summary>The session's SSH tunnel ("SSH tunnel" tab). The password goes to the secret vault.</summary>
public sealed class SshSettings
{
    public bool Active { get; set; }

    /// <summary>plink.exe/ssh.exe: only used in external mode (D7), by explicit choice.</summary>
    public string Executable { get; set; } = string.Empty;

    /// <summary>Empty = the database host.</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string User { get; set; } = string.Empty;

    public string PrivateKeyFile { get; set; } = string.Empty;

    /// <summary>SSH connection timeout in seconds (HeidiSQL: 4, minimum 1).</summary>
    public int Timeout { get; set; } = 4;

    /// <summary>Local forward port; 0 = chosen by the system.</summary>
    public int LocalPort { get; set; }

    /// <summary>Authenticate through the agent (OpenSSH or Pageant) before key and password.</summary>
    public bool UseAgent { get; set; } = true;
}

/// <summary>"SSL" tab (appendix A §1.6). Verification: 0 none, 1 CA, 2 CA + host name (default).</summary>
public sealed class SslSettings
{
    public bool Active { get; set; }

    public string PrivateKey { get; set; } = string.Empty;

    public string CaCertificate { get; set; } = string.Empty;

    public string Certificate { get; set; } = string.Empty;

    public string Cipher { get; set; } = string.Empty;

    public int Verification { get; set; } = 2;
}

/// <summary>"Statistics" tab counters (FAM-S08) and the version seen on the last connection.</summary>
public sealed class SessionStatistics
{
    public DateTimeOffset? Created { get; set; }

    public DateTimeOffset? LastConnect { get; set; }

    public int ConnectCount { get; set; }

    public int RefusedCount { get; set; }

    /// <summary>Full server version on the last connection (Version column and vendor icon).</summary>
    public string ServerVersionFull { get; set; } = string.Empty;
}

/// <summary>
/// A saved session (Settings, SSH tunnel, Advanced, SSL and Statistics tabs of the Session Manager; appendix A §1.6).
/// Passwords are never here: they live in the Credential Manager under the key <see cref="Id"/> (section 23.2).
/// </summary>
public sealed class SessionDefinition
{
    /// <summary>Stable identifier (target of the credentials); does not change on rename or move.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Unnamed";

    /// <summary>Folder path, from the outermost to the innermost (empty = root).</summary>
    public List<string> Folder { get; set; } = [];

    public NetType NetType { get; set; } = NetType.MySqlTcpIp;

    /// <summary>HeidiSQL library (DLL). Kept only for export; the driver is managed.</summary>
    public string Library { get; set; } = string.Empty;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 3306;

    public string User { get; set; } = "root";

    /// <summary>A password is saved in the vault. Without it, the connection asks for it (like "Prompt for credentials").</summary>
    public bool PasswordSaved { get; set; }

    public bool LoginPrompt { get; set; }

    public bool WindowsAuth { get; set; }

    public bool Compressed { get; set; }

    /// <summary>Whitelist separated by <c>;</c> (FAM-S04): replaces <c>SHOW DATABASES</c>.</summary>
    public string Databases { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public SshSettings Ssh { get; set; } = new();

    public SslSettings Ssl { get; set; } = new();

    // Advanced.
    public string StartupScript { get; set; } = string.Empty;

    /// <summary>Client-side execution timeout in seconds (D8); 0 = no limit.</summary>
    public int QueryTimeout { get; set; } = 30;

    /// <summary>Ping every X seconds (keep-alive); 0 turns it off.</summary>
    public int KeepAlive { get; set; } = 20;

    public bool LocalTimeZone { get; set; }

    public bool FullTableStatus { get; set; } = true;

    public bool CleartextAuth { get; set; }

    public bool ForceUnicode { get; set; } = true;

    /// <summary>Session colour 0xRRGGBB, or <c>null</c> (FAM-S02).</summary>
    public uint? Color { get; set; }

    /// <summary>Hide database pattern (regex, FAM-S04).</summary>
    public string HideDatabasePattern { get; set; } = string.Empty;

    public string LogFilePath { get; set; } = string.Empty;

    public bool LogDdl { get; set; }

    public bool LogDml { get; set; }

    /// <summary>
    /// Read-only mode (section 23.2, new): the client only sends reads (<c>ReadOnlyPolicy</c>) and the server is put in
    /// <c>SET SESSION TRANSACTION READ ONLY</c>; the grid does not edit.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Ask before running statements that change data or structure in the query tabs, in Run SQL file and in the CSV
    /// import (phase 14; for production sessions that cannot be read-only).
    /// </summary>
    public bool ConfirmChanges { get; set; }

    /// <summary>Separate metadata connection (12.2); turning it off goes back to HeidiSQL's single-connection mode.</summary>
    public bool MetadataConnection { get; set; } = true;

    /// <summary>Concurrent job connections (12.6).</summary>
    public int JobConnections { get; set; } = 2;

    /// <summary>Last database used (RestoreLastUsedDB).</summary>
    public string LastUsedDatabase { get; set; } = string.Empty;

    public SessionStatistics Statistics { get; set; } = new();

    /// <summary>Path as HeidiSQL shows it: <c>Folder\Sub\Session</c>.</summary>
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
    /// The editable Session Manager fields are equal (without id, name, folder, password and statistics): decides
    /// "Save modifications?" (appendix A §1.5).
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

    /// <summary>Copies the editable fields from <paramref name="source"/> (Save).</summary>
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

    /// <summary>Changes the network type and restores the family defaults in the fields the user did not touch.</summary>
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
