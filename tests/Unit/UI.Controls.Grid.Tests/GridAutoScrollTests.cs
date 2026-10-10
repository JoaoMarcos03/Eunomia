namespace Eunomia.UI.Controls.Grid.Tests;

public sealed class GridAutoScrollTests
{
    [Fact]
    public void InsideTheDeadZoneDoesNotMove()
    {
        var s = new GridAutoScroll();
        s.Start(100, 100);
        s.Move(108, 95);

        Assert.Equal((0L, 0.0), s.Step(1, 18));
    }

    [Fact]
    public void DiagonalMovesRowsAndColumnsWithProportionalSpeed()
    {
        var s = new GridAutoScroll();
        s.Start(100, 100);
        s.Move(100 + GridAutoScroll.DeadZone + 20, 100 + GridAutoScroll.DeadZone + 40);

        var (rows, pixels) = s.Step(0.5, 18);

        Assert.Equal(20 * GridAutoScroll.SpeedPerDip * 0.5, pixels, 6);
        Assert.Equal((long)(40 * GridAutoScroll.SpeedPerDip * 0.5 / 18), rows);
    }

    [Fact]
    public void UpAndLeftAreNegative()
    {
        var s = new GridAutoScroll();
        s.Start(100, 100);
        s.Move(40, 10);

        var (rows, pixels) = s.Step(0.2, 18);

        Assert.True(rows < 0);
        Assert.True(pixels < 0);
    }

    [Fact]
    public void SlowScrollAccumulatesTheRemainder()
    {
        var s = new GridAutoScroll();
        s.Start(0, 0);
        s.Move(0, GridAutoScroll.DeadZone + 1); // 9 DIPs/s = half a row per second

        long total = 0;
        for (var i = 0; i < 60; i++)
        {
            total += s.Step(1 / 60.0, 18).Rows;
        }
        Assert.Equal(0, total);
        for (var i = 0; i < 60; i++)
        {
            total += s.Step(1 / 60.0, 18).Rows;
        }
        Assert.Equal(1, total);
    }

    [Fact]
    public void SpeedHasALimit()
    {
        var s = new GridAutoScroll();
        s.Start(0, 0);
        s.Move(0, 100_000);

        Assert.Equal(GridAutoScroll.MaxSpeed, s.Velocity.Y);
    }

    [Fact]
    public void PressAndReleaseWithoutMovingStaysActive()
    {
        var s = new GridAutoScroll();
        s.Start(50, 50);
        s.Move(52, 51);

        Assert.True(s.Release());
        Assert.True(s.IsActive);
        s.Stop();
        Assert.False(s.IsActive);
        Assert.Equal((0L, 0.0), s.Step(1, 18));
    }

    [Fact]
    public void DragAndReleaseStops()
    {
        var s = new GridAutoScroll();
        s.Start(50, 50);
        s.Move(50, 200);

        Assert.False(s.Release());
        Assert.False(s.IsActive);
    }
}
