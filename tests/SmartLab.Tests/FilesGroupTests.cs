using SmartLab.App.Controls;
using SmartLab.Core.Model;
using SmartLab.Core.Paths;
using SmartLab.Maintenance;
using Xunit;
using Rect = System.Windows.Rect;

namespace SmartLab.Tests;

/// <summary>
/// Treemap geometry, which fails in ways a screenshot does not reveal.
/// </summary>
/// <remarks>
/// Overlapping tiles and tiles that spill past their container both still look like a
/// treemap. The arithmetic is pure and static for exactly this reason, following the
/// gauge's <c>PointOnRing</c>.
/// </remarks>
public sealed class TreemapTests
{
    private static readonly Rect Area = new(0, 0, 400, 300);

    private static IReadOnlyList<(string, long, object?)> Items(params long[] sizes) =>
        sizes.Select((s, i) => ($"item{i}", s, (object?)null)).ToArray();

    [Fact]
    public void TilesStayInsideTheirContainer()
    {
        var tiles = TreemapLayout.Layout(Items(500, 300, 120, 60, 20), Area);

        foreach (var tile in tiles)
        {
            Assert.True(tile.Bounds.Left >= -0.001, $"{tile.Name} starts left of the container");
            Assert.True(tile.Bounds.Top >= -0.001, $"{tile.Name} starts above the container");
            Assert.True(tile.Bounds.Right <= Area.Width + 0.001, $"{tile.Name} spills right");
            Assert.True(tile.Bounds.Bottom <= Area.Height + 0.001, $"{tile.Name} spills below");
        }
    }

    [Fact]
    public void TilesDoNotOverlap()
    {
        var tiles = TreemapLayout.Layout(Items(900, 400, 250, 200, 90, 60, 30), Area);

        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                var overlap = Rect.Intersect(tiles[i].Bounds, tiles[j].Bounds);

                // Shared edges are expected; shared area is not.
                var shared = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;

                Assert.True(shared < 0.5, $"{tiles[i].Name} overlaps {tiles[j].Name}");
            }
        }
    }

    [Fact]
    public void ABiggerItemGetsABiggerTile()
    {
        // The whole point of a treemap. If area does not track size, it is decoration.
        var tiles = TreemapLayout.Layout(Items(800, 400, 200), Area);

        var areas = tiles.Select(t => t.Bounds.Width * t.Bounds.Height).ToArray();

        Assert.True(areas[0] > areas[1]);
        Assert.True(areas[1] > areas[2]);
    }

    [Fact]
    public void AreasAreRoughlyProportionalToSizes()
    {
        var tiles = TreemapLayout.Layout(Items(600, 300), Area);

        var first = tiles[0].Bounds.Width * tiles[0].Bounds.Height;
        var second = tiles[1].Bounds.Width * tiles[1].Bounds.Height;

        // Twice the bytes, about twice the area. Loose, because the last row absorbs
        // rounding on purpose rather than leaving a bare strip.
        Assert.InRange(first / second, 1.7, 2.3);
    }

    [Fact]
    public void ZeroSizedItemsDrawNothing()
    {
        // A negative or infinite rectangle from a zero divisor would throw inside
        // WPF's renderer, taking the window rather than the tile.
        var tiles = TreemapLayout.Layout(Items(0, 0, 0), Area);

        Assert.Empty(tiles);
    }

    [Fact]
    public void AnEmptyContainerDrawsNothing()
    {
        Assert.Empty(TreemapLayout.Layout(Items(100, 50), new Rect(0, 0, 0, 0)));
    }

    [Fact]
    public void SliversAreDroppedRatherThanDrawn()
    {
        // One huge item and many tiny ones. The tiny tiles would be sub-pixel bands
        // nobody can see or click, and drawing thousands of them costs real time.
        var sizes = new long[201];
        sizes[0] = 10_000_000;
        for (var i = 1; i < sizes.Length; i++) sizes[i] = 1;

        var tiles = TreemapLayout.Layout(Items(sizes), Area);

        Assert.True(tiles.Count < sizes.Length);
    }

    [Fact]
    public void HitTestFindsTheTileUnderAPoint()
    {
        var tiles = TreemapLayout.Layout(Items(700, 300), Area);
        var first = tiles[0];

        var centre = new System.Windows.Point(
            first.Bounds.X + first.Bounds.Width / 2,
            first.Bounds.Y + first.Bounds.Height / 2);

        Assert.Same(first, TreemapLayout.HitTest(tiles, centre));
    }

    [Fact]
    public void HitTestOutsideEverythingFindsNothing()
    {
        var tiles = TreemapLayout.Layout(Items(700, 300), Area);

        Assert.Null(TreemapLayout.HitTest(tiles, new System.Windows.Point(9999, 9999)));
    }
}

/// <summary>The size and age thresholds, at their boundaries.</summary>
public sealed class LargeOldFileTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

    private static FileEntry Entry(long length, DateTimeOffset? written) =>
        new(ExtendedPath.From(@"C:\data\file.bin"), "file.bin", length,
            EntryAttributes.Normal, written);

    [Fact]
    public void AFileExactlyAtTheSizeThresholdQualifies()
    {
        var entry = Entry(100 * 1024 * 1024, Now.AddYears(-1));

        Assert.True(LargeOldFileScanner.Qualifies(entry, 100 * 1024 * 1024, TimeSpan.FromDays(180), Now));
    }

    [Fact]
    public void AFileOneByteUnderDoesNot()
    {
        var entry = Entry(100 * 1024 * 1024 - 1, Now.AddYears(-1));

        Assert.False(LargeOldFileScanner.Qualifies(entry, 100 * 1024 * 1024, TimeSpan.FromDays(180), Now));
    }

    [Fact]
    public void ABigButRecentFileIsNotOffered()
    {
        var entry = Entry(4L * 1024 * 1024 * 1024, Now.AddDays(-3));

        Assert.False(LargeOldFileScanner.Qualifies(entry, 100 * 1024 * 1024, TimeSpan.FromDays(180), Now));
    }

    [Fact]
    public void AFileWithNoReadableTimestampIsAdmittedOnSizeAlone()
    {
        // Corrupt entries carry timestamps that will not convert. Discarding a 4 GB
        // file because its clock is unreadable hides the thing being looked for.
        var entry = Entry(4L * 1024 * 1024 * 1024, written: null);

        Assert.True(LargeOldFileScanner.Qualifies(entry, 100 * 1024 * 1024, TimeSpan.FromDays(180), Now));
    }

    [Fact]
    public void ADirectoryIsNeverAFile()
    {
        var entry = new FileEntry(
            ExtendedPath.From(@"C:\data"), "data", 0, EntryAttributes.Directory, Now.AddYears(-5));

        Assert.False(LargeOldFileScanner.Qualifies(entry, 0, TimeSpan.Zero, Now));
    }

    [Theory]
    [InlineData(6L * 1024 * 1024 * 1024, "Over 5 GB")]
    [InlineData(2L * 1024 * 1024 * 1024, "1 to 5 GB")]
    [InlineData(700L * 1024 * 1024, "500 MB to 1 GB")]
    [InlineData(120L * 1024 * 1024, "Under 500 MB")]
    public void BracketsSplitBySize(long bytes, string expected)
    {
        var file = new LargeFile(@"C:\f", "f", bytes, TimeSpan.FromDays(400));

        Assert.Equal(expected, file.Bracket);
    }

    [Fact]
    public void TheHeadingNamesWhichClockItRead()
    {
        // Windows stops updating last-access time by default, so "not opened in two
        // years" is a claim the filesystem cannot support.
        var summary = SmartLab.App.LargeOldFilesViewModel.Summarise(12, 0, "4.2 GB");

        Assert.Contains("last written", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The Shredder's refusals, which are the deliverable.
/// </summary>
/// <remarks>
/// The overwrite loop is ten lines and cannot be undone once it runs. Deciding what
/// must never reach it is the part worth testing.
/// </remarks>
public sealed class SecureDeleteTests
{
    [Fact]
    public void ADriveRootIsRefused()
    {
        Assert.True(SecureDelete.IsRefused(@"C:\", null, out var reason));
        Assert.Contains("root", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheWindowsFolderIsRefused()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.True(SecureDelete.IsRefused(Path.Combine(windows, "System32", "kernel32.dll"), null, out _));
    }

    [Fact]
    public void TheVolumeBeingRecoveredIsRefused()
    {
        // The mirror of the rule the recovery destination already carries: this app
        // must not destroy data on the volume it is reading back.
        Assert.True(SecureDelete.IsRefused(@"E:\work\file.bin", @"E:\", out var reason));
        Assert.Contains("Deleted files", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnOrdinaryFileOnAnotherVolumeIsAllowed()
    {
        Assert.False(SecureDelete.IsRefused(@"D:\scratch\file.bin", @"E:\", out _));
    }

    [Fact]
    public void AnEmptyPathIsRefused()
    {
        Assert.True(SecureDelete.IsRefused("", null, out _));
    }

    [Fact]
    public void DryRunWritesNothingAndDeletesNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartlab-shred-{Guid.NewGuid():N}.bin");
        var original = new byte[4096];
        Random.Shared.NextBytes(original);

        File.WriteAllBytes(path, original);

        try
        {
            var result = SecureDelete.Shred(path, passes: 3, ShredConfidence.Unknown, dryRun: true);

            Assert.False(result.Deleted);
            Assert.True(File.Exists(path));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ARealShredOverwritesAndRemovesTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartlab-shred-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[8192]);

        try
        {
            var result = SecureDelete.Shred(path, passes: 1, ShredConfidence.Overwritten, dryRun: false);

            Assert.True(result.Deleted, result.Error);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AnUnknownDriveTypeNeverClaimsDestruction()
    {
        // Guessing "rotating" on a failed query would be the one way this section
        // could lie about what it achieved.
        Assert.Equal(ShredConfidence.Unknown, SecureDelete.ConfidenceFor(null));
        Assert.Equal(ShredConfidence.NotGuaranteed, SecureDelete.ConfidenceFor(isSolidState: true));
        Assert.Equal(ShredConfidence.Overwritten, SecureDelete.ConfidenceFor(isSolidState: false));
    }

    [Theory]
    [InlineData(ShredConfidence.NotGuaranteed)]
    [InlineData(ShredConfidence.Unknown)]
    public void AnythingButRotatingMediaSaysSoInPlainWords(ShredConfidence confidence)
    {
        var caveat = SmartLab.App.ShredderViewModel.DriveCaveat(confidence);

        Assert.Contains("cannot", caveat, StringComparison.OrdinalIgnoreCase);
    }
}
