using UsbDoctor.Maintenance;
using Xunit;

namespace UsbDoctor.Tests;

public class JunkCatalogueTests
{
    private static readonly IReadOnlyList<JunkCategory> Catalogue = JunkCatalogue.ForCurrentUser();

    /// <summary>
    /// The Recycle Bin belongs to its own section and must not also be offered here.
    /// </summary>
    /// <remarks>
    /// Two screens proposing the same irreversible deletion is how one of them ends up
    /// with the wrong default. Trash Bins owns it, per drive, every row unticked.
    /// </remarks>
    [Fact]
    public void The_recycle_bin_is_not_a_junk_category()
    {
        Assert.DoesNotContain(Catalogue, c => c.IsRecycleBin);
        Assert.DoesNotContain(Catalogue, c => c.Id == "recycle-bin");
    }

    [Fact]
    public void Every_category_that_carries_a_caution_starts_unticked()
    {
        // A warning next to a pre-ticked box is decoration, not a warning.
        foreach (var category in Catalogue.Where(c => c.Caution is not null))
            Assert.False(category.EnabledByDefault, $"'{category.Name}' is ticked despite a caution.");
    }

    [Fact]
    public void Browser_categories_name_only_cache_directories()
    {
        // Signing the user out of everything to reclaim disk space is not a trade
        // anyone asked for, so cookies, logins and history must never appear.
        string[] forbidden = ["Cookies", "Login Data", "History", "Bookmarks", "Web Data"];

        var browserPaths = Catalogue
            .Where(c => c.Id.Contains("chrome") || c.Id.Contains("edge") || c.Id.Contains("firefox"))
            .SelectMany(c => c.Locations);

        foreach (var path in browserPaths)
        {
            foreach (var name in forbidden)
                Assert.DoesNotContain(name, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Nothing_targets_a_drive_root_or_a_whole_profile()
    {
        // A category pointed at a root would empty the machine.
        foreach (var location in Catalogue.SelectMany(c => c.Locations))
        {
            var trimmed = location.TrimEnd('\\');

            Assert.True(trimmed.Length > 3, $"'{location}' is too close to a drive root.");
            Assert.NotEqual(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'),
                trimmed, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\'),
                trimmed, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Category_ids_are_unique()
    {
        // Findings are matched back to rows by id, so a duplicate would silently
        // update the wrong row's size.
        Assert.Equal(Catalogue.Count, Catalogue.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
    }
}

public class JunkScannerTests
{
    private static JunkCategory Category(string id, params string[] locations) =>
        new(id, id, "detail", locations);

    [Fact]
    public void Sizes_and_counts_are_summed_across_a_categorys_locations()
    {
        var probe = new FakeTraceProbe();
        probe.Directories.Add(@"C:\a");
        probe.Directories.Add(@"C:\b");
        probe.Sizes[@"C:\a"] = 1_000;
        probe.Sizes[@"C:\b"] = 2_500;
        probe.FileCounts[@"C:\a"] = 3;
        probe.FileCounts[@"C:\b"] = 4;

        var finding = Assert.Single(
            new JunkScanner(probe).Scan([Category("x", @"C:\a", @"C:\b")]));

        Assert.Equal(3_500, finding.Bytes);
        Assert.Equal(7, finding.Files);
    }

    [Fact]
    public void A_location_that_does_not_exist_contributes_nothing()
    {
        var probe = new FakeTraceProbe();
        probe.Directories.Add(@"C:\a");
        probe.Sizes[@"C:\a"] = 500;

        var finding = Assert.Single(
            new JunkScanner(probe).Scan([Category("x", @"C:\a", @"C:\missing")]));

        Assert.Equal(500, finding.Bytes);
    }

    [Fact]
    public void An_empty_category_reads_as_empty_rather_than_zero_bytes()
    {
        var finding = Assert.Single(new JunkScanner(new FakeTraceProbe()).Scan([Category("x", @"C:\gone")]));

        Assert.Equal(0, finding.Bytes);
        Assert.Equal("empty", finding.SizeText);
    }

    [Fact]
    public void The_recycle_bin_is_measured_through_the_shell_not_the_filesystem()
    {
        var probe = new FakeTraceProbe { RecycleBinBytes = 9_876_543 };

        var category = new JunkCategory("recycle-bin", "Recycle Bin", "d", []) { IsRecycleBin = true };
        var finding = Assert.Single(new JunkScanner(probe).Scan([category]));

        Assert.Equal(9_876_543, finding.Bytes);
    }

    /// <summary>
    /// Contents, never the directory itself: removing %TEMP% outright breaks every
    /// program that expects it to exist.
    /// </summary>
    [Fact]
    public void Traces_target_directory_contents()
    {
        var finding = new JunkFinding(Category("x", @"C:\a", @"C:\b"), 100, 2);

        var traces = JunkScanner.ToTraces([finding]);

        Assert.Equal(2, traces.Count);
        Assert.All(traces, t => Assert.Equal(TraceKind.DirectoryContents, t.Kind));
    }

    [Fact]
    public void The_recycle_bin_becomes_its_own_trace_kind()
    {
        var category = new JunkCategory("recycle-bin", "Recycle Bin", "d", []) { IsRecycleBin = true };

        var trace = Assert.Single(JunkScanner.ToTraces([new JunkFinding(category, 500, 2)]));

        Assert.Equal(TraceKind.RecycleBin, trace.Kind);
    }

    [Theory]
    [InlineData(0, "empty")]
    [InlineData(512 * 1024, "512 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    public void Sizes_read_in_the_unit_a_person_would_use(long bytes, string expected)
    {
        Assert.Equal(expected, new JunkFinding(Category("x"), bytes, 0).SizeText);
    }
}
