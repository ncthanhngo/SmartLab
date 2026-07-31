using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace SmartLab.App.Controls;

/// <summary>
/// Draws a treemap of sized items and reports clicks on them.
/// </summary>
/// <remarks>
/// Rendered directly rather than templated, following <see cref="RadialGauge"/>: the
/// geometry is built by hand either way, and a template over hundreds of tiles would
/// add an element tree per rectangle for nothing.
/// </remarks>
public sealed class Treemap : FrameworkElement
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(Treemap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    public static readonly DependencyProperty HairlineBrushProperty = DependencyProperty.Register(
        nameof(HairlineBrush), typeof(Brush), typeof(Treemap),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(Treemap),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Items must expose Name and Bytes; anything else is ignored.</summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public Brush HairlineBrush
    {
        get => (Brush)GetValue(HairlineBrushProperty);
        set => SetValue(HairlineBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <summary>
    /// Run when a tile is clicked, with that tile's payload as the parameter.
    /// </summary>
    /// <remarks>
    /// A command rather than an event, because the map is built inside a DataTemplate
    /// where there is no code-behind to attach a handler to.
    /// </remarks>
    public static readonly DependencyProperty TileCommandProperty = DependencyProperty.Register(
        nameof(TileCommand), typeof(ICommand), typeof(Treemap), new PropertyMetadata(null));

    public ICommand? TileCommand
    {
        get => (ICommand?)GetValue(TileCommandProperty);
        set => SetValue(TileCommandProperty, value);
    }

    private IReadOnlyList<TreemapTile> _tiles = [];
    private TreemapTile? _hover;

    /// <summary>
    /// Hues cycled across tiles.
    /// </summary>
    /// <remarks>
    /// Fixed rather than palette-driven, because these are large filled areas that
    /// need only to be distinguishable from their neighbours. Held at a single
    /// lightness so no tile reads as more important than another - the area already
    /// carries the magnitude.
    /// </remarks>
    private static readonly Color[] Hues =
    [
        Color.FromRgb(0x3E, 0x7B, 0xC4), Color.FromRgb(0x2E, 0x9E, 0x8F),
        Color.FromRgb(0xC4, 0x8A, 0x2E), Color.FromRgb(0x9C, 0x5A, 0xC4),
        Color.FromRgb(0xC4, 0x4E, 0x6E), Color.FromRgb(0x4E, 0xA5, 0x5C),
        Color.FromRgb(0x5A, 0x6E, 0xC4), Color.FromRgb(0xC4, 0x6B, 0x3E),
    ];

    public Treemap()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    /// <remarks>
    /// The collection has to be listened to, not just the property. A view model that
    /// clears and refills one ObservableCollection never changes the property, so
    /// without this the map draws once and then silently shows the first folder
    /// forever - which looks like a measurement that produced nothing.
    /// </remarks>
    private static void OnItemsChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        var map = (Treemap)source;

        if (e.OldValue is INotifyCollectionChanged old)
            old.CollectionChanged -= map.OnCollectionChanged;

        if (e.NewValue is INotifyCollectionChanged fresh)
            fresh.CollectionChanged += map.OnCollectionChanged;

        map._hover = null;
        map.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = TreemapLayout.HitTest(_tiles, e.GetPosition(this));
        if (ReferenceEquals(hit, _hover)) return;

        _hover = hit;
        ToolTip = hit is null ? null : $"{hit.Name}  -  {Format(hit.Bytes)}";

        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (TreemapLayout.HitTest(_tiles, e.GetPosition(this)) is not { } hit) return;

        if (TileCommand is { } command && command.CanExecute(hit.Payload))
            command.Execute(hit.Payload);
    }

    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        // A transparent fill so the whole surface takes hit tests, not just the tiles.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        _tiles = TreemapLayout.Layout(Read(), new Rect(0, 0, ActualWidth, ActualHeight));
        if (_tiles.Count == 0) return;

        var hairline = new Pen(HairlineBrush, 1);

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            var fill = new SolidColorBrush(Hues[i % Hues.Length])
            {
                Opacity = ReferenceEquals(tile, _hover) ? 1.0 : 0.78,
            };

            fill.Freeze();

            context.DrawRectangle(fill, hairline, tile.Bounds);
            DrawLabel(context, tile);
        }
    }

    /// <remarks>
    /// Labels are drawn only where they fit whole. A clipped name is worse than none:
    /// it reads as a different folder.
    /// </remarks>
    private void DrawLabel(DrawingContext context, TreemapTile tile)
    {
        if (tile.Bounds.Width < 56 || tile.Bounds.Height < 26) return;

        var text = new FormattedText(
            tile.Name, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold,
                FontStretches.Normal),
            11, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, tile.Bounds.Width - 10),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        context.DrawText(text, new Point(tile.Bounds.X + 5, tile.Bounds.Y + 4));

        if (tile.Bounds.Height < 42) return;

        var size = new FormattedText(
            Format(tile.Bytes), CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, tile.Bounds.Width - 10),
            MaxLineCount = 1,
        };

        context.DrawText(size, new Point(tile.Bounds.X + 5, tile.Bounds.Y + 19));
    }

    private List<(string, long, object?)> Read()
    {
        var items = new List<(string, long, object?)>();
        if (ItemsSource is null) return items;

        foreach (var item in ItemsSource)
        {
            if (item is null) continue;

            var type = item.GetType();
            var name = type.GetProperty("Name")?.GetValue(item) as string;
            var bytes = type.GetProperty("Bytes")?.GetValue(item);

            if (name is null || bytes is not long size) continue;

            items.Add((name, size, item));
        }

        return items;
    }

    private static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height);
}
