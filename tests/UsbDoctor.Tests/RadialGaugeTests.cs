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
}
