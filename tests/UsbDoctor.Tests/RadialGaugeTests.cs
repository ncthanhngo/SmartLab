using UsbDoctor.App.Controls;
using Xunit;
using Point = System.Windows.Point;

namespace UsbDoctor.Tests;

/// <summary>
/// The gauge's angle convention: clockwise, starting at twelve o'clock.
/// </summary>
/// <remarks>
/// Rendering is not tested here - only the arithmetic that decides where the arc
/// begins and ends. A ring that starts at three o'clock still looks like a ring,
/// which is exactly why the convention needs pinning down in a test rather than
/// left to whoever next looks at the screen.
/// </remarks>
public sealed class RadialGaugeTests
{
    private static readonly Point Centre = new(100, 100);
    private const double Radius = 50;
    private const double Tolerance = 1e-9;

    [Fact]
    public void ZeroDegrees_IsDirectlyAboveTheCentre()
    {
        var point = RadialGauge.PointOnRing(Centre, Radius, degreesFromTop: 0);

        Assert.Equal(100, point.X, Tolerance);
        Assert.Equal(50, point.Y, Tolerance);
    }

    [Fact]
    public void NinetyDegrees_IsToTheRight_ConfirmingClockwise()
    {
        // The whole point of the test: counter-clockwise would put this on the left.
        var point = RadialGauge.PointOnRing(Centre, Radius, degreesFromTop: 90);

        Assert.Equal(150, point.X, Tolerance);
        Assert.Equal(100, point.Y, Tolerance);
    }

    [Fact]
    public void OneEightyDegrees_IsBelowTheCentre()
    {
        var point = RadialGauge.PointOnRing(Centre, Radius, degreesFromTop: 180);

        Assert.Equal(100, point.X, Tolerance);
        Assert.Equal(150, point.Y, Tolerance);
    }

    [Fact]
    public void ThreeSixtyDegrees_ReturnsToTheStart()
    {
        var start = RadialGauge.PointOnRing(Centre, Radius, degreesFromTop: 0);
        var full = RadialGauge.PointOnRing(Centre, Radius, degreesFromTop: 360);

        Assert.Equal(start.X, full.X, Tolerance);
        Assert.Equal(start.Y, full.Y, Tolerance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(190)]
    [InlineData(359)]
    public void EveryAngle_StaysOnTheCircle(double degrees)
    {
        var point = RadialGauge.PointOnRing(Centre, Radius, degrees);

        var distance = Math.Sqrt(
            Math.Pow(point.X - Centre.X, 2) + Math.Pow(point.Y - Centre.Y, 2));

        Assert.Equal(Radius, distance, 1e-9);
    }

    // ---- the sweep --------------------------------------------------------------

    [Fact]
    public void ALongerTravel_TakesLonger()
    {
        // The ring moves at a speed, not on a timer. Without this a category being
        // ticked would take as long to register as a gauge filling from empty.
        var full = RadialGauge.DurationFor(0, 1);
        var half = RadialGauge.DurationFor(0, 0.5);

        Assert.True(half < full);
    }

    [Fact]
    public void NoChange_DoesNotSweep()
    {
        Assert.Equal(TimeSpan.Zero, RadialGauge.DurationFor(0.4, 0.4));
    }

    [Fact]
    public void ATinyChange_StillLastsLongEnoughToBeSeenMoving()
    {
        // A one-percent nudge scaled strictly by distance would last a few
        // milliseconds, which reads as the number flickering rather than the ring
        // travelling.
        var tiny = RadialGauge.DurationFor(0.62, 0.63);

        Assert.True(tiny >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void FallingTakesAsLongAsRising()
    {
        // Emptying happens when the operator unticks something, and an emptying ring
        // that snaps back would read as the value having been discarded.
        Assert.Equal(RadialGauge.DurationFor(0.2, 0.8), RadialGauge.DurationFor(0.8, 0.2));
    }

    [Fact]
    public void ValuesBeyondTheRange_TravelOnlyAsFarAsTheRingCan()
    {
        // Percent is clamped when drawn, so a caller handing over 3.0 must not buy
        // three times the sweep for a ring that stops at full.
        Assert.Equal(RadialGauge.DurationFor(0, 1), RadialGauge.DurationFor(0, 3));
    }
}
