namespace SmartLab.Fat;

/// <summary>An entry found by walking raw on-disk structures.</summary>
/// <param name="Path">Path relative to the volume root.</param>
/// <param name="FirstCluster">Starting cluster, or 0 when the entry has no data.</param>
/// <param name="IsContiguous">
/// True when the data is known to occupy consecutive clusters, so it can be read
/// without consulting the allocation table.
/// </param>
public sealed record RawEntry(
    string Path,
    string Name,
    bool IsDirectory,
    bool IsDeleted,
    uint FirstCluster,
    long Length,
    bool IsContiguous = false);

/// <summary>How much of a carved region is still unallocated.</summary>
public enum RecoveryConfidence
{
    /// <summary>Allocation state could not be read, so nothing can be said.</summary>
    Unknown,

    /// <summary>Every cluster is still free. The data is most likely intact.</summary>
    Likely,

    /// <summary>Some clusters have been reallocated. The file is partly another file.</summary>
    Partial,

    /// <summary>Every cluster belongs to something else. Carving would return foreign data.</summary>
    Overwritten,

    /// <summary>
    /// The clusters are in use, but by a live entry starting at the same place:
    /// the data is intact and already reachable under another name.
    /// </summary>
    Superseded,
}

/// <summary>
/// Distinguishes clusters genuinely reused by another file from clusters still
/// holding the same data under a newer directory entry.
/// </summary>
/// <remarks>
/// Observed on a live drive: a rescue that moved files to the volume root left
/// their old entries deleted while the new entries pointed at the same clusters.
/// The allocation table honestly reports those clusters as in use, so a naive
/// reading calls the data overwritten and skips it - yet carving them returned
/// byte-identical copies of the surviving files. Matching the deleted entry's
/// starting cluster against live entries separates the two cases.
/// </remarks>
public static class DeletedEntryAssessor
{
    public static RecoveryConfidence Refine(
        RecoveryConfidence measured, uint firstCluster, IReadOnlySet<uint> liveFirstClusters) =>
        measured is RecoveryConfidence.Overwritten or RecoveryConfidence.Partial &&
        liveFirstClusters.Contains(firstCluster)
            ? RecoveryConfidence.Superseded
            : measured;
}

/// <param name="TotalClusters">Clusters the file would occupy if unfragmented.</param>
/// <param name="FreeClusters">Of those, how many the filesystem still considers free.</param>
public sealed record ClusterRangeAssessment(
    int TotalClusters, int FreeClusters, int InUseClusters, int UnknownClusters)
{
    public static ClusterRangeAssessment None { get; } = new(0, 0, 0, 0);

    /// <summary>
    /// Turns raw counts into a verdict.
    /// </summary>
    /// <remarks>
    /// A cluster still marked free is one the filesystem has not handed to anyone
    /// since the delete, so its contents are very likely the original file's. A
    /// cluster marked in-use belongs to a live file, and reading it back returns
    /// that file's bytes, not the deleted one's. This is the difference between a
    /// recovery worth attempting and one that produces convincing garbage.
    /// </remarks>
    public RecoveryConfidence Confidence
    {
        get
        {
            if (TotalClusters == 0) return RecoveryConfidence.Unknown;
            if (UnknownClusters == TotalClusters) return RecoveryConfidence.Unknown;
            if (InUseClusters == 0) return RecoveryConfidence.Likely;
            if (FreeClusters == 0) return RecoveryConfidence.Overwritten;
            return RecoveryConfidence.Partial;
        }
    }

    public string Summary => Confidence switch
    {
        RecoveryConfidence.Likely => $"all {TotalClusters} cluster(s) still free",
        RecoveryConfidence.Partial => $"{InUseClusters} of {TotalClusters} cluster(s) reallocated",
        RecoveryConfidence.Overwritten => $"all {TotalClusters} cluster(s) reallocated",
        _ => "allocation state unavailable",
    };

    /// <summary>Summary for a confidence refined by <see cref="DeletedEntryAssessor"/>.</summary>
    public string SummaryFor(RecoveryConfidence confidence) =>
        confidence == RecoveryConfidence.Superseded
            ? $"{TotalClusters} cluster(s) in use by a live entry at the same start - data intact"
            : Summary;
}

/// <summary>
/// Read-only access to a filesystem parsed directly from device sectors.
/// </summary>
/// <remarks>
/// Implemented for FAT32 and exFAT. The point of going below the mounted
/// filesystem is to see what it will not show: entries marked deleted, whose name,
/// starting cluster and length usually survive the deletion.
/// </remarks>
public interface IRawFileSystem
{
    /// <summary>Human-readable geometry summary, for reports.</summary>
    string Describe();

    int BytesPerCluster { get; }

    IEnumerable<RawEntry> EnumerateTree(bool includeDeleted = true, int maxDepth = 64);

    /// <summary>
    /// Reads a deleted entry's data by assuming its clusters are consecutive.
    /// </summary>
    /// <remarks>
    /// Deletion clears the allocation-table entries, so the chain that described
    /// where the file actually lived is gone. Reading forward from the starting
    /// cluster is the only option left, and it is correct exactly when the file was
    /// not fragmented. The bytes returned may belong to a file written since, which
    /// is why recovery output must never overwrite anything and must be treated as
    /// a candidate rather than a result.
    /// </remarks>
    byte[] ReadContiguous(uint firstCluster, long length);

    /// <summary>
    /// Reports how many clusters in the range a carve would read are still free.
    /// </summary>
    /// <remarks>
    /// Lets the caller tell a recovery worth keeping from one that will return
    /// another file's bytes, instead of leaving the operator to guess.
    /// </remarks>
    ClusterRangeAssessment AssessRange(uint firstCluster, long length);
}

/// <summary>Opens whichever supported filesystem a device holds.</summary>
public static class RawFileSystem
{
    public static bool TryOpen(Stream stream, out IRawFileSystem? fileSystem, out string? error)
    {
        if (Fat32Reader.TryOpen(stream, out var fat32, out var fatError))
        {
            fileSystem = fat32;
            error = null;
            return true;
        }

        if (ExFatReader.TryOpen(stream, out var exfat, out var exfatError))
        {
            fileSystem = exfat;
            error = null;
            return true;
        }

        fileSystem = null;
        error = $"Not FAT32 ({fatError}); not exFAT ({exfatError}).";
        return false;
    }
}
