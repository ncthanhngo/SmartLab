using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
/// <para>
/// The arc sweeps to a new value rather than jumping to it. A dial that snaps
/// states its number without ever showing that it changed, and the moment the
/// figure lands is the only moment the operator is looking at it.
/// </para>
/// </remarks>
public sealed class RadialGauge : FrameworkElement
{
    /// <summary>How long a full 0-to-1 sweep takes.</summary>
    private static readonly TimeSpan FullSweep = TimeSpan.FromMilliseconds(620);

    /// <summary>
    /// Floor on the sweep, so a small correction still reads as movement.
    /// </summary>
    /// <remarks>
    /// Below roughly a tenth of a second the eye registers a jump rather than a
    /// travel, which would make a ring that re-measures during a scan flicker.
    /// </remarks>
    private static readonly TimeSpan MinimumSweep = TimeSpan.FromMilliseconds(140);

    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(0.0, OnPercentChanged));

    /// <summary>
    /// What is actually drawn: the animated value chasing <see cref="Percent"/>.
    /// </summary>
    /// <remarks>
    /// A separate property because an animation has to own the value it drives.
    /// Animating <see cref="Percent"/> itself would leave the binding fighting the
    /// storyboard - a bound value that is also animated is read back as the animated
    /// one, so the next update would start from wherever the sweep happened to be.
    /// </remarks>
    public static readonly DependencyProperty RenderedPercentProperty = DependencyProperty.Register(
        nameof(RenderedPercent), typeof(double), typeof(RadialGauge),
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

    /// <summary>The value on screen this instant, mid-sweep.</summary>
    public double RenderedPercent
    {
        get => (double)GetValue(RenderedPercentProperty);
        set => SetValue(RenderedPercentProperty, value);
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
    /// How long the ring should take to travel between two values.
    /// </summary>
    /// <remarks>
    /// Proportional to the distance, so the sweep reads at one speed rather than at
    /// one duration: a ring nudged from 0.62 to 0.64 as a category is ticked must
    /// not take as long as one filling from empty, or the interface feels sluggish
    /// exactly where it is doing the least.
    /// </remarks>
    public static TimeSpan DurationFor(double from, double to)
    {
        var distance = Math.Abs(Math.Clamp(to, 0, 1) - Math.Clamp(from, 0, 1));
        if (distance <= 0) return TimeSpan.Zero;

        var scaled = FullSweep * distance;

        return scaled < MinimumSweep ? MinimumSweep : scaled;
    }

    private static void OnPercentChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RadialGauge)source;

        var from = gauge.RenderedPercent;
        var to = (double)e.NewValue;
        var duration = DurationFor(from, to);

        if (duration == TimeSpan.Zero)
        {
            // Clearing the animation first: a held storyboard outranks a local
            // value, so setting the property under one has no visible effect.
            gauge.BeginAnimation(RenderedPercentProperty, null);
            gauge.RenderedPercent = to;
            return;
        }

        // Eased out rather than linear. A ring decelerating into its final value
        // reads as settling on a measurement; a constant sweep reads as a timer.
        gauge.BeginAnimation(RenderedPercentProperty, new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
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

        var percent = Math.Clamp(RenderedPercent, 0, 1);
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
