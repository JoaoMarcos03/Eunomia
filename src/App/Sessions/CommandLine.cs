using System.Globalization;

namespace Eunomia.App.Sessions;

/// <summary>Pedido de ligação vindo da linha de comandos (FAM-S06; anexo A §9).</summary>
public sealed record CommandLineRequest
{
    /// <summary><c>-d</c> / <c>--description</c>: caminho de uma sessão gravada.</summary>
    public string? SessionPath { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? User { get; init; }

    public string? Password { get; init; }

    public string? Databases { get; init; }

    public int? NetType { get; init; }

    public bool? WindowsAuth { get; init; }

    public bool? Ssl { get; init; }

    public string? SshHost { get; init; }

    public int? SshPort { get; init; }

    public string? SshUser { get; init; }

    public string? SshPassword { get; init; }

    public string? SshKey { get; init; }

    /// <summary>Ficheiros <c>.sql</c> a abrir em query tabs.</summary>
    public IReadOnlyList<string> SqlFiles { get; init; } = [];

    /// <summary>Algum parâmetro de ligação (então cria-se uma sessão ad hoc por cima da <c>-d</c> ou dos defaults).</summary>
    public bool HasConnectionOverrides =>
        Host is not null || Port is not null || User is not null || Password is not null || Databases is not null || NetType is not null || SshHost is not null;

    public bool IsEmpty => SessionPath is null && !HasConnectionOverrides && SqlFiles.Count == 0;
}

/// <summary>
/// Linha de comandos compatível com o HeidiSQL (FAM-S06): <c>-x=valor</c>, <c>--longo=valor</c>, <c>-x valor</c> e
/// <c>-x"valor"</c>; um valor não pode começar por <c>-</c>. Parâmetros de ligação criam uma sessão ad hoc
/// (<c>host-xxxx</c>) que não é gravada. Opções próprias (<c>--theme</c>, <c>--startup-probe</c>…) são ignoradas aqui.
/// </summary>
public static class CommandLine
{
    private static readonly (string Short, string Long)[] Known =
    [
        ("d", "description"), ("h", "host"), ("P", "port"), ("u", "user"), ("p", "password"), ("db", "databases"),
        ("n", "nettype"), ("W", "winauth"), ("ssl", "ssl"), ("sh", "ssh-host"), ("sP", "ssh-port"), ("su", "ssh-user"),
        ("sp", "ssh-password"), ("sk", "ssh-key"), ("l", "library"), ("S", "socket"),
    ];

    public static CommandLineRequest Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
            {
                if (arg.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(arg);
                }
                continue;
            }
            var (name, inline) = SplitArgument(arg);
            var key = Known.FirstOrDefault(k => k.Short == name || k.Long == name).Long;
            if (key is null)
            {
                continue;
            }
            var value = inline;
            if (value is null && i + 1 < args.Count && !args[i + 1].StartsWith('-'))
            {
                value = args[++i];
            }
            if (value is not null)
            {
                values[key] = value;
            }
        }
        string? V(string key) => values.TryGetValue(key, out var v) ? v : null;
        int? I(string key) => int.TryParse(V(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        bool? B(string key) => V(key) is { } b ? b is "1" or "true" : null;
        return new CommandLineRequest
        {
            SessionPath = V("description"),
            Host = V("host") ?? V("socket"),
            Port = I("port"),
            User = V("user"),
            Password = V("password"),
            Databases = V("databases"),
            NetType = I("nettype") is { } n and > 0 ? n : V("socket") is not null ? 1 : null,
            WindowsAuth = B("winauth"),
            Ssl = B("ssl"),
            SshHost = V("ssh-host"),
            SshPort = I("ssh-port"),
            SshUser = V("ssh-user"),
            SshPassword = V("ssh-password"),
            SshKey = V("ssh-key"),
            SqlFiles = files,
        };
    }

    /// <summary>Sessão a ligar: a gravada (<c>-d</c>) com os parâmetros por cima, ou uma ad hoc nova.</summary>
    public static SessionDefinition? Resolve(CommandLineRequest request, SessionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        var saved = request.SessionPath is { } path ? catalog.FindByPath(path) : null;
        if (!request.HasConnectionOverrides)
        {
            return saved;
        }
        var session = saved?.Clone() ?? new SessionDefinition();
        if (saved is not null)
        {
            session.Id = Guid.NewGuid();
        }
        if (request.NetType is { } n && Enum.IsDefined(typeof(NetType), n))
        {
            session.ChangeNetType((NetType)n);
        }
        session.Host = request.Host ?? session.Host;
        session.Port = request.Port ?? session.Port;
        session.User = request.User ?? session.User;
        session.Databases = request.Databases ?? session.Databases;
        session.WindowsAuth = request.WindowsAuth ?? session.WindowsAuth;
        session.Ssl.Active = request.Ssl ?? session.Ssl.Active;
        if (request.SshHost is not null)
        {
            session.Ssh.Active = true;
            session.Ssh.Host = request.SshHost;
            session.Ssh.Port = request.SshPort ?? session.Ssh.Port;
            session.Ssh.User = request.SshUser ?? session.Ssh.User;
            session.Ssh.PrivateKeyFile = request.SshKey ?? session.Ssh.PrivateKeyFile;
        }
        session.Folder = [];
        session.Name = $"{session.Host}-{Guid.NewGuid().ToString("N")[..4]}";
        return session;
    }

    private static (string Name, string? Value) SplitArgument(string arg)
    {
        var body = arg.TrimStart('-');
        var eq = body.IndexOf('=', StringComparison.Ordinal);
        if (eq >= 0)
        {
            return (body[..eq], Unquote(body[(eq + 1)..]));
        }
        var quote = body.IndexOf('"', StringComparison.Ordinal);
        return quote > 0 ? (body[..quote], Unquote(body[quote..])) : (body, null);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
