namespace UsbDoctor.Fat;

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
