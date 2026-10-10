namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Scroll livre com o botão do meio (autoscroll do Windows), pedido pelo utilizador a 1 out 2026 (não está
/// na spec). O botão do meio fixa uma origem; a velocidade, em qualquer direção (incluindo diagonal),
/// cresce com a distância do rato à origem, com uma zona morta à volta. Dois modos, como no Windows:
/// carregar e largar sem mexer fica ativo até ao próximo clique ou tecla; carregar e arrastar para quando
/// se larga o botão. Sem estado de UI: o controlo chama <see cref="Step"/> em cada frame.
/// </summary>
public sealed class GridAutoScroll
{
    /// <summary>Raio da zona morta em DIPs: dentro dela não há scroll.</summary>
    public const double DeadZone = 12;

    /// <summary>Velocidade por DIP de distância fora da zona morta, em DIPs por segundo.</summary>
    public const double SpeedPerDip = 9;

    /// <summary>Velocidade máxima em DIPs por segundo (~ 300 linhas/s com linhas de 18 px).</summary>
    public const double MaxSpeed = 6000;

    private double _originX;
    private double _originY;
    private double _pointerX;
    private double _pointerY;
    private double _restY;
    private bool _dragged;

    public bool IsActive { get; private set; }

    /// <summary>Origem do scroll (onde se desenha o marcador), em coordenadas do controlo.</summary>
    public (double X, double Y) Origin => (_originX, _originY);

    /// <summary>Botão do meio carregado: ativa o scroll com origem no ponteiro.</summary>
    public void Start(double x, double y)
    {
        IsActive = true;
        _originX = _pointerX = x;
        _originY = _pointerY = y;
        _restY = 0;
        _dragged = false;
    }

    public void Move(double x, double y)
    {
        _pointerX = x;
        _pointerY = y;
        if (Math.Abs(x - _originX) > DeadZone || Math.Abs(y - _originY) > DeadZone)
        {
            _dragged = true;
        }
    }

    /// <summary>
    /// Botão do meio largado. Se o rato foi arrastado para fora da zona morta, o scroll para;
    /// se não, fica ativo até <see cref="Stop"/>. Devolve se continua ativo.
    /// </summary>
    public bool Release()
    {
        if (_dragged)
        {
            Stop();
        }
        return IsActive;
    }

    public void Stop() => IsActive = false;

    /// <summary>Velocidade atual em DIPs por segundo (x para a direita, y para baixo).</summary>
    public (double X, double Y) Velocity => (Speed(_pointerX - _originX), Speed(_pointerY - _originY));

    /// <summary>
    /// Avança <paramref name="seconds"/>. Devolve as linhas e os DIPs horizontais a deslocar neste frame;
    /// o resto fracionário fica para o frame seguinte, para o scroll lento não se perder.
    /// </summary>
    public (long Rows, double Pixels) Step(double seconds, double rowHeight)
    {
        if (!IsActive || seconds <= 0 || rowHeight <= 0)
        {
            return (0, 0);
        }
        var (vx, vy) = Velocity;
        var pixels = vx * seconds;
        _restY += vy * seconds / rowHeight;
        // Tolerância para a soma de frações (120 × 1/120 dá 0,9999…) não perder a linha.
        var rows = (long)Math.Truncate(_restY + (Math.Sign(_restY) * 1e-9));
        _restY -= rows;
        return (rows, pixels);
    }

    private static double Speed(double distance)
    {
        var beyond = Math.Abs(distance) - DeadZone;
        if (beyond <= 0)
        {
            return 0;
        }
        return Math.Sign(distance) * Math.Min(MaxSpeed, beyond * SpeedPerDip);
    }
}
