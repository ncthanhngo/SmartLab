using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// WinForms is referenced for the tray icon, which makes these ambiguous.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;

namespace SmartLab.App.Controls;

/// <summary>
/// One measured figure: the number, its unit, what it counts, and — only where a
/// denominator genuinely exists — a bar showing the share.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the dial in every section that had no honest proportion to draw. Five of
/// the seventeen were drawing a ring pinned at full, which states a fraction that does
/// not exist; a figure with no bar states only what it knows.
/// </para>
/// <para>
/// Set in the mono face with tabular figures, so a size that changes from 9.9 GB to
/// 10.1 GB does not shift the layout underneath it.
/// </para>
/// </remarks>
public sealed class Reading : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(Reading), new PropertyMetadata(string.Empty));

    /// <summary>Shown small beside the value: GB, files, threats.</summary>
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(Reading), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(Reading), new PropertyMetadata(string.Empty));

    /// <summary>0 to 1. Drawn only when <see cref="ShowProportion"/> is set.</summary>
    public static readonly DependencyProperty ProportionProperty = DependencyProperty.Register(
        nameof(Proportion), typeof(double), typeof(Reading), new PropertyMetadata(0.0));

    /// <summary>
    /// Whether this figure has a real denominator.
    /// </summary>
    /// <remarks>
    /// Explicit rather than inferred from a non-zero proportion: a genuine 0% - nothing
    /// ticked out of plenty measured - still deserves its empty bar, and a figure with
    /// no denominator must never grow one by accident.
    /// </remarks>
    public static readonly DependencyProperty ShowProportionProperty = DependencyProperty.Register(
        nameof(ShowProportion), typeof(bool), typeof(Reading), new PropertyMetadata(false));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(Reading), new PropertyMetadata(Brushes.Gray));

    /// <summary>Context beside a headline figure, set smaller so it cannot be mistaken for one.</summary>
    public static readonly DependencyProperty IsQuietProperty = DependencyProperty.Register(
        nameof(IsQuiet), typeof(bool), typeof(Reading), new PropertyMetadata(false));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public double Proportion
    {
        get => (double)GetValue(ProportionProperty);
        set => SetValue(ProportionProperty, value);
    }

    public bool ShowProportion
    {
        get => (bool)GetValue(ShowProportionProperty);
        set => SetValue(ShowProportionProperty, value);
    }

    public Brush Tint
    {
        get => (Brush)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public bool IsQuiet
    {
        get => (bool)GetValue(IsQuietProperty);
        set => SetValue(IsQuietProperty, value);
    }

    static Reading()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(Reading), new FrameworkPropertyMetadata(typeof(Reading)));
    }
}

/// <summary>
/// The filled part of a <see cref="Reading"/>'s proportion bar.
/// </summary>
/// <remarks>
/// A drawn element rather than a Grid with a star column, because the share has to
/// animate and a column width cannot be animated smoothly. It sweeps for the same
/// reason the gauge did: the moment a figure lands is the only moment anyone is
/// looking at it.
/// </remarks>
public sealed class ProportionBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProportionBar),
        new FrameworkPropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty RenderedValueProperty = DependencyProperty.Register(
        nameof(RenderedValue), typeof(double), typeof(ProportionBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ProportionBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(ProportionBar),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double RenderedValue
    {
        get => (double)GetValue(RenderedValueProperty);
        set => SetValue(RenderedValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    private static void OnValueChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        var bar = (ProportionBar)source;

        var from = bar.RenderedValue;
        var to = (double)e.NewValue;
        var duration = RadialGauge.DurationFor(from, to);

        if (duration == TimeSpan.Zero)
        {
            bar.BeginAnimation(RenderedValueProperty, null);
            bar.RenderedValue = to;
            return;
        }

        bar.BeginAnimation(RenderedValueProperty,
            new System.Windows.Media.Animation.DoubleAnimation(from, to, duration)
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                },
            });
    }

    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var radius = ActualHeight / 2;

        context.DrawRoundedRectangle(
            Track, null, new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);

        var share = Math.Clamp(RenderedValue, 0, 1);
        if (share <= 0) return;

        // Never narrower than its own height: a 1% share drawn as a 2 px sliver reads
        // as a rendering fault rather than as a small number.
        var width = Math.Max(ActualHeight, share * ActualWidth);

        context.DrawRoundedRectangle(
            Fill, null, new Rect(0, 0, width, ActualHeight), radius, radius);
    }
}
