using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

public class JunkCatalogueTests
{
    private static readonly IReadOnlyList<JunkCategory> Catalogue = JunkCatalogue.ForCurrentUser();

    /// <summary>
    /// The Recycle Bin belongs to its own section and must not also be offered here.
    /// </summary>
    /// <remarks>
    /// Two screens proposing the same irreversible deletion is how one of them ends up
    /// with the wrong default. Recycle Bins owns it, per drive, every row unticked.
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

    private static AppTrace Contents() =>
        new(TraceKind.DirectoryContents, @"C:\WINDOWS\SoftwareDistribution\Download", "Windows Update cache");

    [Fact]
    public void A_sweep_that_removed_nothing_at_all_is_a_failure()
    {
        // Measured against this machine: unelevated, the Windows Update cache refuses
        // all 49,499 files and the section reported "Cleaned. 7.44 GB still held",
        // with no failures, over a folder it had not touched a byte of.
        var result = Win32TraceRemover.Describe(Contents(), removed: 0, locked: 0, refused: 49_499);

        Assert.Equal(RemovalOutcome.Failed, result.Outcome);
        Assert.Contains("Administrator", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_and_in_use_are_different_answers()
    {
        // A locked file frees itself; a refused one needs Administrator and never
        // will. Reporting both as "still in use" tells the operator to wait for
        // something that is not going to happen.
        var refused = Win32TraceRemover.Describe(Contents(), removed: 3, locked: 0, refused: 2);
        var locked = Win32TraceRemover.Describe(Contents(), removed: 3, locked: 2, refused: 0);

        Assert.Contains("Administrator", refused.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Administrator", locked.Detail, StringComparison.Ordinal);
        Assert.Contains("in use", locked.Detail, StringComparison.Ordinal);

        // The flag, not the sentence, is what decides whether a UAC prompt is offered.
        Assert.True(refused.RefusedPermission);
        Assert.False(locked.RefusedPermission);
    }

    [Fact]
    public void Removing_some_of_it_still_counts_as_removed()
    {
        // Locked files are normal on a live machine. A partial sweep is not a failure,
        // or every clean of a temp folder in use would report as one.
        var result = Win32TraceRemover.Describe(Contents(), removed: 40, locked: 2, refused: 0);

        Assert.Equal(RemovalOutcome.Removed, result.Outcome);
    }

    [Fact]
    public void Only_category_ids_cross_the_elevation_boundary()
    {
        // The property the whole design rests on. The elevated process derives the
        // folder from the catalogue itself, so a forged argument can only name a
        // category this app was already prepared to empty - never a path of its own.
        var arguments = ElevatedCleanup.BuildArguments(["windows-temp", "windows-update"]);

        Assert.DoesNotContain(":", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", arguments, StringComparison.Ordinal);
        Assert.Equal("--clean windows-temp,windows-update", arguments);
    }

    [Fact]
    public void An_id_that_is_not_in_the_catalogue_is_dropped()
    {
        // Both sides check. This one keeps a typo from reaching an elevated process;
        // the worker's own Resolve is the check that matters, since this side is the
        // one an attacker would replace.
        Assert.Equal(string.Empty, ElevatedCleanup.BuildArguments(["../../windows", "no-such-category"]));
        Assert.Empty(ElevatedCleanup.Resolve("no-such-category"));
        Assert.Empty(ElevatedCleanup.Resolve(null));
    }

    [Fact]
    public void A_resolved_category_is_the_catalogue_entry_itself()
    {
        var resolved = Assert.Single(ElevatedCleanup.Resolve("windows-update"));

        Assert.Equal("windows-update", resolved.Id);
        Assert.True(resolved.NeedsElevation);
        Assert.NotEmpty(resolved.Locations);
    }

    [Fact]
    public void Naming_nothing_asks_for_no_prompt()
    {
        // An empty argument string is what stops the section raising UAC for a job
        // with nothing in it.
        Assert.Equal(string.Empty, ElevatedCleanup.BuildArguments([]));
    }

    [Fact]
    public void An_empty_folder_is_not_a_failure()
    {
        // Nothing removed because there was nothing to remove. The counts that make a
        // failure are the ones that say something was left behind.
        Assert.Equal(
            RemovalOutcome.Removed,
            Win32TraceRemover.Describe(Contents(), removed: 0, locked: 0, refused: 0).Outcome);
    }
}
