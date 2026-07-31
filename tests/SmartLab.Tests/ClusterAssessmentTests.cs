using SmartLab.Fat;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// Covers telling a recovery worth keeping from one that returns another file's
/// bytes. Without this the operator is left guessing, which on a 10 GB carved
/// archive is an expensive guess.
/// </summary>
public class ClusterAssessmentTests
{
    // ---- FAT32: a cluster is free precisely when its FAT entry is zero --------

    [Fact]
    public void Fat32_reports_likely_when_every_cluster_is_still_free()
    {
        var builder = new Fat32ImageBuilder()
            .AddFile(2, "GONE.DAT", FatAttributes.Archive, 40, 1200, deleted: true);

        builder.EndChain(2);

        Assert.True(Fat32Reader.TryOpen(builder.BuildDeviceStream(), out var reader, out _));

        // Cluster size is 512 here, so 1200 bytes spans three clusters, none of
        // which has been handed to anyone since the delete.
        var assessment = reader!.AssessRange(40, 1200);

        Assert.Equal(3, assessment.TotalClusters);
        Assert.Equal(3, assessment.FreeClusters);
        Assert.Equal(RecoveryConfidence.Likely, assessment.Confidence);
    }

    [Fact]
    public void Fat32_reports_overwritten_when_every_cluster_was_reallocated()
    {
        var builder = new Fat32ImageBuilder();
        builder.SetFatEntry(40, 41).SetFatEntry(41, 42).EndChain(42);

        Assert.True(Fat32Reader.TryOpen(builder.BuildDeviceStream(), out var reader, out _));

        var assessment = reader!.AssessRange(40, 1200);

        Assert.Equal(3, assessment.InUseClusters);
        Assert.Equal(0, assessment.FreeClusters);
        Assert.Equal(RecoveryConfidence.Overwritten, assessment.Confidence);
    }

    [Fact]
    public void Fat32_reports_partial_when_only_some_clusters_came_back()
    {
        var builder = new Fat32ImageBuilder();
        builder.SetFatEntry(41, 0x0FFFFFFF); // middle cluster taken by a live file

        Assert.True(Fat32Reader.TryOpen(builder.BuildDeviceStream(), out var reader, out _));

        var assessment = reader!.AssessRange(40, 1200);

        Assert.Equal(RecoveryConfidence.Partial, assessment.Confidence);
        Assert.Equal(1, assessment.InUseClusters);
        Assert.Equal(2, assessment.FreeClusters);
        Assert.Contains("1 of 3", assessment.Summary, StringComparison.Ordinal);
    }

    // ---- exFAT: the allocation bitmap is the authority ------------------------

    private static ExFatReader OpenExFat(ExFatImageBuilder builder)
    {
        Assert.True(ExFatReader.TryOpen(builder.BuildDeviceStream(), out var reader, out var error), error);
        return reader!;
    }

    [Fact]
    public void ExFat_reads_the_allocation_bitmap_and_reports_free_clusters()
    {
        var builder = new ExFatImageBuilder()
            .AddAllocationBitmap(bitmapCluster: 3, lengthBytes: 64)
            .AddEntry(2, "gone.bin", isDirectory: false, 40, 1024, deleted: true);

        builder.EndChain(2);

        // Cluster size is 512, so 1024 bytes spans two clusters, both left free.
        var assessment = OpenExFat(builder).AssessRange(40, 1024);

        Assert.Equal(2, assessment.TotalClusters);
        Assert.Equal(2, assessment.FreeClusters);
        Assert.Equal(RecoveryConfidence.Likely, assessment.Confidence);
    }

    [Fact]
    public void ExFat_reports_overwritten_when_the_bitmap_marks_the_range_in_use()
    {
        var builder = new ExFatImageBuilder()
            .AddAllocationBitmap(bitmapCluster: 3, lengthBytes: 64)
            .AddEntry(2, "gone.bin", isDirectory: false, 40, 1024, deleted: true)
            .SetClusterAllocated(40, true)
            .SetClusterAllocated(41, true);

        builder.EndChain(2);

        var assessment = OpenExFat(builder).AssessRange(40, 1024);

        Assert.Equal(2, assessment.InUseClusters);
        Assert.Equal(RecoveryConfidence.Overwritten, assessment.Confidence);
    }

    [Fact]
    public void ExFat_reports_partial_when_one_cluster_was_taken()
    {
        var builder = new ExFatImageBuilder()
            .AddAllocationBitmap(bitmapCluster: 3, lengthBytes: 64)
            .AddEntry(2, "gone.bin", isDirectory: false, 40, 1024, deleted: true)
            .SetClusterAllocated(41, true);

        builder.EndChain(2);

        var assessment = OpenExFat(builder).AssessRange(40, 1024);

        Assert.Equal(RecoveryConfidence.Partial, assessment.Confidence);
        Assert.Equal(1, assessment.FreeClusters);
        Assert.Equal(1, assessment.InUseClusters);
    }

    [Fact]
    public void ExFat_without_a_bitmap_reports_unknown_rather_than_guessing()
    {
        // A volume whose bitmap cannot be located must not be described as safe to
        // recover from. Absence of evidence is not evidence of a free cluster.
        var builder = new ExFatImageBuilder()
            .AddEntry(2, "gone.bin", isDirectory: false, 40, 1024, deleted: true);

        var assessment = OpenExFat(builder).AssessRange(40, 1024);

        Assert.Equal(RecoveryConfidence.Unknown, assessment.Confidence);
        Assert.Equal(2, assessment.UnknownClusters);
    }

    // ---- shared ---------------------------------------------------------------

    [Theory]
    [InlineData(0u, 100L)]   // no starting cluster
    [InlineData(40u, 0L)]    // no length
    public void An_unusable_range_assesses_to_nothing(uint cluster, long length)
    {
        Assert.True(Fat32Reader.TryOpen(new Fat32ImageBuilder().BuildDeviceStream(), out var reader, out _));

        var assessment = reader!.AssessRange(cluster, length);

        Assert.Equal(0, assessment.TotalClusters);
        Assert.Equal(RecoveryConfidence.Unknown, assessment.Confidence);
    }

    /// <summary>
    /// Regression from a live drive: after a rescue moved files to the volume
    /// root, their old entries were deleted while the new ones pointed at the same
    /// clusters. The allocation table honestly reported those clusters as in use,
    /// so the range measured as Overwritten and was skipped - yet carving it
    /// returned byte-identical copies of the surviving files.
    /// </summary>
    [Fact]
    public void A_deleted_entry_superseded_by_a_live_one_is_not_called_overwritten()
    {
        IReadOnlySet<uint> live = new HashSet<uint> { 1567 };

        Assert.Equal(RecoveryConfidence.Superseded,
            DeletedEntryAssessor.Refine(RecoveryConfidence.Overwritten, 1567, live));

        Assert.Equal(RecoveryConfidence.Superseded,
            DeletedEntryAssessor.Refine(RecoveryConfidence.Partial, 1567, live));
    }

    [Fact]
    public void A_genuinely_reused_range_stays_overwritten()
    {
        IReadOnlySet<uint> live = new HashSet<uint> { 999 };

        Assert.Equal(RecoveryConfidence.Overwritten,
            DeletedEntryAssessor.Refine(RecoveryConfidence.Overwritten, 1567, live));
    }

    [Fact]
    public void Refinement_never_downgrades_a_free_range()
    {
        // A range with no clusters in use is unaffected by what live files claim.
        IReadOnlySet<uint> live = new HashSet<uint> { 1567 };

        Assert.Equal(RecoveryConfidence.Likely,
            DeletedEntryAssessor.Refine(RecoveryConfidence.Likely, 1567, live));

        Assert.Equal(RecoveryConfidence.Unknown,
            DeletedEntryAssessor.Refine(RecoveryConfidence.Unknown, 1567, live));
    }

    [Fact]
    public void Superseded_summary_explains_the_data_is_intact()
    {
        var assessment = new ClusterRangeAssessment(3, 0, 3, 0);

        Assert.Contains("data intact",
            assessment.SummaryFor(RecoveryConfidence.Superseded), StringComparison.Ordinal);
        Assert.Contains("reallocated",
            assessment.SummaryFor(RecoveryConfidence.Overwritten), StringComparison.Ordinal);
    }

    [Fact]
    public void Confidence_summaries_describe_what_was_measured()
    {
        Assert.Equal("all 4 cluster(s) still free",
            new ClusterRangeAssessment(4, 4, 0, 0).Summary);

        Assert.Equal("all 4 cluster(s) reallocated",
            new ClusterRangeAssessment(4, 0, 4, 0).Summary);

        Assert.Equal("allocation state unavailable",
            new ClusterRangeAssessment(4, 0, 0, 4).Summary);
    }
}
