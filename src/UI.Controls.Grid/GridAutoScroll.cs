namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Free scroll with the middle button (Windows autoscroll), requested by the author on 1 Oct 2026 (it is not
/// in the spec). The middle button sets an origin; the speed, in any direction (diagonal included),
/// grows with the mouse's distance from the origin, with a dead zone around it. Two modes, as on Windows:
/// press and release without moving stays active until the next click or key; press and drag stops when
/// the button is released. No UI state: the control calls <see cref="Step"/> on every frame.
/// </summary>
public sealed class GridAutoScroll
{
    /// <summary>Dead zone radius in DIPs: no scrolling inside it.</summary>
    public const double DeadZone = 12;

    /// <summary>Speed per DIP of distance outside the dead zone, in DIPs per second.</summary>
    public const double SpeedPerDip = 9;

    /// <summary>Maximum speed in DIPs per second (~ 300 rows/s with 18 px rows).</summary>
    public const double MaxSpeed = 6000;

    private double _originX;
    private double _originY;
    private double _pointerX;
    private double _pointerY;
    private double _restY;
    private bool _dragged;

    public bool IsActive { get; private set; }

    /// <summary>Scroll origin (where the marker is drawn), in control coordinates.</summary>
    public (double X, double Y) Origin => (_originX, _originY);

    /// <summary>Middle button pressed: activates scrolling with the origin at the pointer.</summary>
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
    /// Middle button released. If the mouse was dragged out of the dead zone, scrolling stops;
    /// otherwise, it stays active until <see cref="Stop"/>. Returns whether it is still active.
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

    /// <summary>Current speed in DIPs per second (x to the right, y downwards).</summary>
    public (double X, double Y) Velocity => (Speed(_pointerX - _originX), Speed(_pointerY - _originY));

    /// <summary>
    /// Advances <paramref name="seconds"/>. Returns the rows and horizontal DIPs to move in this frame;
    /// the fractional remainder is kept for the next frame, so slow scrolling is not lost.
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
        // Tolerance so the sum of fractions (120 × 1/120 gives 0.9999…) does not lose the row.
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
