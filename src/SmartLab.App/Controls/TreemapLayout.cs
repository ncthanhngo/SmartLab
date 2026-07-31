using Rect = System.Windows.Rect;

namespace SmartLab.App.Controls;

/// <summary>One laid-out tile: what it represents and where it goes.</summary>
public sealed record TreemapTile(string Name, long Bytes, Rect Bounds, object? Payload = null);

/// <summary>
/// Squarified treemap geometry.
/// </summary>
/// <remarks>
/// <para>
/// Pure and static, like <see cref="RadialGauge.PointOnRing"/> and for the same
/// reason: the arithmetic that decides whether tiles overlap or leave gaps can be
/// tested exactly, without constructing a visual or looking at a screen.
/// </para>
/// <para>
/// Squarified rather than sliced. A slice-and-dice layout gives a 40 GB folder and a
/// 40 MB one the same width and differing heights by three orders of magnitude, so
/// the small one becomes a hairline nobody can click. Squarifying keeps aspect ratios
/// near one, which is what makes areas comparable by eye.
/// </para>
/// </remarks>
public static class TreemapLayout
{
    /// <summary>
    /// Tiles smaller than this on either side are merged rather than drawn.
    /// </summary>
    /// <remarks>
    /// A disk has thousands of directories and a panel has a few hundred pixels.
    /// Drawing every one produces a band of invisible slivers that costs layout time
    /// and says nothing; they are worth more as one honest "smaller items" tile.
    /// </remarks>
    public const double MinimumSide = 6;

    /// <summary>
    /// Lays items out inside <paramref name="bounds"/>, largest first.
    /// </summary>
    /// <param name="items">Name, size and payload. Sizes are sorted here, not by the caller.</param>
    public static IReadOnlyList<TreemapTile> Layout(
        IReadOnlyList<(string Name, long Bytes, object? Payload)> items, Rect bounds)
    {
        var tiles = new List<TreemapTile>();

        if (bounds.Width <= 0 || bounds.Height <= 0) return tiles;

        var ordered = items.Where(i => i.Bytes > 0).OrderByDescending(i => i.Bytes).ToList();
        if (ordered.Count == 0) return tiles;

        var total = ordered.Sum(i => i.Bytes);
        if (total <= 0) return tiles;

        Squarify(ordered, total, bounds, tiles);

        return tiles;
    }

    /// <summary>
    /// Fills a rectangle by laying rows along its shorter side.
    /// </summary>
    /// <remarks>
    /// The shorter side is what keeps the tiles square-ish: a row laid along the long
    /// edge of a wide rectangle produces tall thin slivers, which is the failure the
    /// squarified algorithm exists to avoid.
    /// </remarks>
    private static void Squarify(
        List<(string Name, long Bytes, object? Payload)> items,
        long total, Rect area, List<TreemapTile> tiles)
    {
        var index = 0;

        while (index < items.Count && area is { Width: > 0, Height: > 0 })
        {
            var vertical = area.Width >= area.Height;
            var shortSide = vertical ? area.Height : area.Width;

            // Grow the row while adding the next item makes its tiles squarer.
            var rowBytes = items[index].Bytes;
            var count = 1;
            var best = WorstRatio(items, index, count, rowBytes, total, area, shortSide);

            while (index + count < items.Count)
            {
                var nextBytes = rowBytes + items[index + count].Bytes;
                var ratio = WorstRatio(items, index, count + 1, nextBytes, total, area, shortSide);

                if (ratio > best) break;

                best = ratio;
                rowBytes = nextBytes;
                count++;
            }

            var areaShare = (double)rowBytes / total;
            var rowThickness = areaShare * (vertical ? area.Width : area.Height);

            // The final row takes whatever is left rather than its computed share, so
            // rounding cannot leave a bare strip along the edge.
            var isLast = index + count >= items.Count;
            if (isLast) rowThickness = vertical ? area.Width : area.Height;

            var offset = 0.0;

            for (var i = 0; i < count; i++)
            {
                var item = items[index + i];
                var share = rowBytes > 0 ? (double)item.Bytes / rowBytes : 0;
                var extent = share * shortSide;

                // Last tile in the row absorbs the remainder, same reason.
                if (i == count - 1) extent = shortSide - offset;

                var rect = vertical
                    ? new Rect(area.X, area.Y + offset, rowThickness, extent)
                    : new Rect(area.X + offset, area.Y, extent, rowThickness);

                if (rect.Width >= MinimumSide && rect.Height >= MinimumSide)
                    tiles.Add(new TreemapTile(item.Name, item.Bytes, rect, item.Payload));

                offset += extent;
            }

            area = vertical
                ? new Rect(area.X + rowThickness, area.Y, Math.Max(0, area.Width - rowThickness), area.Height)
                : new Rect(area.X, area.Y + rowThickness, area.Width, Math.Max(0, area.Height - rowThickness));

            total -= rowBytes;
            index += count;

            if (total <= 0) break;
        }
    }

    /// <summary>Worst aspect ratio in a candidate row. Lower is squarer.</summary>
    private static double WorstRatio(
        List<(string Name, long Bytes, object? Payload)> items,
        int start, int count, long rowBytes, long total, Rect area, double shortSide)
    {
        if (rowBytes <= 0 || shortSide <= 0) return double.MaxValue;

        var longSide = area.Width >= area.Height ? area.Width : area.Height;
        var thickness = (double)rowBytes / total * longSide;

        if (thickness <= 0) return double.MaxValue;

        var worst = 0.0;

        for (var i = 0; i < count; i++)
        {
            var bytes = items[start + i].Bytes;
            if (bytes <= 0) continue;

            var extent = (double)bytes / rowBytes * shortSide;
            if (extent <= 0) return double.MaxValue;

            worst = Math.Max(worst, Math.Max(thickness / extent, extent / thickness));
        }

        return worst == 0 ? double.MaxValue : worst;
    }

    /// <summary>The tile under a point, or null. Topmost last, as they were drawn.</summary>
    public static TreemapTile? HitTest(IReadOnlyList<TreemapTile> tiles, System.Windows.Point point)
    {
        for (var i = tiles.Count - 1; i >= 0; i--)
            if (tiles[i].Bounds.Contains(point)) return tiles[i];

        return null;
    }
}
