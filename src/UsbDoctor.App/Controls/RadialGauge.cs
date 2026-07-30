using System.Globalization;
using System.Windows;
using System.Windows.Media;

// WinForms is referenced for the tray icon, which puts System.Drawing in scope and
// makes Brush, Point and Size ambiguous. The aliases pin them to WPF's versions.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace UsbDoctor.App.Controls;

/// <summary>
/// A ring gauge: a full track with an arc drawn over it.
/// </summary>
/// <remarks>
/// <para>
/// Rendered directly rather than templated. WPF has no conic gradient, so the arc
/// has to be a stroked <see cref="ArcSegment"/> either way, and once the geometry
/// is being built by hand a control template adds a layer that does nothing.
/// </para>
/// <para>
/// The arc starts at twelve o'clock and runs clockwise, which is the direction
/// people read a dial. Round caps, because a flat cap on a thick ring reads as a
/// cut rather than a value.
/// </para>
/// </remarks>
public sealed class RadialGauge : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RadialGauge),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(RadialGauge),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 to 1. Values outside that range are clamped rather than rejected.</summary>
    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ValueBrush
    {
        get => (Brush)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    /// <summary>
    /// A point on the ring, measured clockwise from twelve o'clock.
    /// </summary>
    /// <remarks>
    /// Static and pure so the geometry can be tested without constructing a visual.
    /// Screen Y grows downward, which is why the sine term is subtracted.
    /// </remarks>
    public static Point PointOnRing(Point centre, double radius, double degreesFromTop)
    {
        var radians = (degreesFromTop - 90) * Math.PI / 180.0;

        return new Point(
            centre.X + (radius * Math.Cos(radians)),
            centre.Y + (radius * Math.Sin(radians)));
    }

    protected override void OnRender(DrawingContext context)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;

        var thickness = Math.Min(Thickness, side / 2);
        var radius = (side - thickness) / 2;
        if (radius <= 0) return;

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);

        context.DrawEllipse(
            null, new Pen(TrackBrush, thickness), centre, radius, radius);

        var percent = Math.Clamp(Percent, 0, 1);
        if (percent <= 0) return;

        var pen = new Pen(ValueBrush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        // A full ring cannot be drawn as one arc - start and end coincide and the
        // segment collapses - so it falls back to the ellipse it actually is.
        if (percent >= 0.999)
        {
            context.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }

        var sweep = percent * 360;
        var start = PointOnRing(centre, radius, 0);
        var end = PointOnRing(centre, radius, sweep);

        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(start, isFilled: false, isClosed: false);
            figure.ArcTo(
                end, new Size(radius, radius), rotationAngle: 0,
                isLargeArc: sweep > 180, SweepDirection.Clockwise,
                isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Square by nature. Without this a gauge in a stretching panel becomes an
        // ellipse, which reads as a rendering fault rather than a design choice.
        var side = Math.Min(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

        return side > 0 ? new Size(side, side) : new Size(160, 160);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"RadialGauge {Percent:P0}");
}
