using System.Globalization;

namespace Eunomia.Presentation.Shell;

/// <summary>
/// Os 7 painéis da status bar (secção 5.3, anexo C §1.6, FAM-L06): 0 info, 1 posição <c>r:c (sel)</c>,
/// 2 "Connected: …", 3 tipo e versão do servidor, 4 "Uptime: …", 5 "Server time: …" com barra de progresso por
/// cima, 6 "Idle." ou a mensagem da operação em curso.
/// </summary>
public sealed class StatusBarModel : ObservableObject
{
    public const string IdleText = "Idle.";

    private string _info = string.Empty;
    private string _position = string.Empty;
    private string _connected = "Disconnected.";
    private string _server = string.Empty;
    private string _uptime = string.Empty;
    private string _serverTime = string.Empty;
    private string _busy = IdleText;
    private double? _progress;
    private string? _serverTooltip;
    private string _transaction = string.Empty;

    public string Info { get => _info; set => Set(ref _info, value); }

    public string Position { get => _position; set => Set(ref _position, value); }

    public string Connected { get => _connected; set => Set(ref _connected, value); }

    /// <summary>
    /// Estado da transação da sessão ativa (secção 14.5): vazio, "Transaction open" ou "Uncommitted changes"; quando não
    /// está vazio, o painel 2 mostra-o em vez do tempo de ligação, com fundo de aviso.
    /// </summary>
    public string Transaction { get => _transaction; set => Set(ref _transaction, value); }

    public bool HasUncommittedChanges => _transaction == "Uncommitted changes";

    public string Server { get => _server; set => Set(ref _server, value); }

    /// <summary>Hover do painel 3: pares chave/valor da ligação.</summary>
    public string? ServerTooltip { get => _serverTooltip; set => Set(ref _serverTooltip, value); }

    public string Uptime { get => _uptime; set => Set(ref _uptime, value); }

    public string ServerTime { get => _serverTime; set => Set(ref _serverTime, value); }

    /// <summary>Painel 6; qualquer texto diferente de <see cref="IdleText"/> mostra a ampulheta.</summary>
    public string Busy { get => _busy; set => Set(ref _busy, value); }

    public bool IsBusy => !string.Equals(Busy, IdleText, StringComparison.Ordinal);

    /// <summary>Progresso 0–1 desenhado por cima do painel 5; <c>null</c> esconde a barra.</summary>
    public double? Progress { get => _progress; set => Set(ref _progress, value); }

    public void SetIdle()
    {
        Busy = IdleText;
        Progress = null;
    }

    public void ShowDisconnected()
    {
        Connected = "Disconnected.";
        Transaction = string.Empty;
        Server = string.Empty;
        ServerTooltip = null;
        Uptime = string.Empty;
        ServerTime = string.Empty;
    }

    public void ShowConnection(TimeSpan connectedFor, TimeSpan? serverUptime, DateTimeOffset? serverNow)
    {
        Connected = "Connected: " + DurationFormatter.Format(connectedFor);
        Uptime = "Uptime: " + (serverUptime is { } u ? DurationFormatter.Format(u) : "unknown");
        ServerTime = serverNow is { } t ? "Server time: " + t.ToString("t", CultureInfo.CurrentCulture) : string.Empty;
    }
}

/// <summary>
/// Durações como no HeidiSQL ("Connected: 4 days, 00:10 h", "Uptime: 10 days, 06:52 h"). Abaixo de um dia
/// mostra também os segundos ("00:05:12 h").
/// </summary>
public static class DurationFormatter
{
    public static string Format(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }
        var days = (int)duration.TotalDays;
        return days switch
        {
            0 => string.Create(CultureInfo.InvariantCulture, $"{duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00} h"),
            1 => string.Create(CultureInfo.InvariantCulture, $"1 day, {duration.Hours:00}:{duration.Minutes:00} h"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{days} days, {duration.Hours:00}:{duration.Minutes:00} h"),
        };
    }
}
