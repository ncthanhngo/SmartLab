using System.Buffers.Binary;

namespace SmartLab.Fat;

/// <summary>
/// Reads FAT32 structures directly from a byte stream.
/// </summary>
/// <remarks>
/// <para>
/// Works over any <see cref="Stream"/>, so the same code reads a live volume
/// opened as <c>\\.\E:</c> and a synthetic image in memory. That is what makes
/// the format handling testable: producing the pathological cases this tool
/// exists for — a deleted entry, a cross-linked chain, a name of raw bytes —
/// means writing 32 bytes into an array, not sourcing a damaged USB stick.
/// </para>
/// <para>
/// Opening a live volume this way requires Administrator. The parsing below does
/// not, which is why the two concerns are separate types.
/// </para>
/// </remarks>
public sealed class Fat32Reader : IRawFileSystem
{
    private readonly SectorReader _sectors;
    private readonly Fat32BootSector _boot;

    private const uint EndOfChain = 0x0FFFFFF8;
    private const uint BadCluster = 0x0FFFFFF7;

    /// <summary>Bounds the walk so a corrupt chain cannot loop forever.</summary>
    private const int MaxClustersPerChain = 1 << 20;

    private Fat32Reader(Stream stream, Fat32BootSector boot)
    {
        _boot = boot;
        _sectors = new SectorReader(stream, boot.BytesPerSector);
    }

    public Fat32BootSector BootSector => _boot;

    public int BytesPerCluster => _boot.BytesPerCluster;

    public string Describe() =>
        $"FAT32  {_boot.BytesPerSector} B/sector  {_boot.SectorsPerCluster} sector(s)/cluster  " +
        $"{_boot.BytesPerCluster} B/cluster  root cluster {_boot.RootCluster}";

    /// <summary>
    /// Assesses a carve range using the FAT itself.
    /// </summary>
    /// <remarks>
    /// FAT32 has no separate allocation bitmap: a cluster is free precisely when
    /// its FAT entry is zero. Deleting a file zeroes its chain, so a range that
    /// reads back as all zeroes is one nothing has claimed since.
    /// </remarks>
    public ClusterRangeAssessment AssessRange(uint firstCluster, long length)
    {
        if (firstCluster < 2 || length <= 0) return ClusterRangeAssessment.None;

        var clusters = (int)Math.Min(
            MaxClustersPerChain,
            (length + _boot.BytesPerCluster - 1) / _boot.BytesPerCluster);

        int free = 0, inUse = 0, unknown = 0;
        Span<byte> entry = stackalloc byte[4];

        for (var i = 0; i < clusters; i++)
        {
            var cluster = firstCluster + (uint)i;

            if (!_sectors.TryRead(_boot.FatOffset + ((long)cluster * 4), entry))
            {
                unknown++;
                continue;
            }

            if ((BinaryPrimitives.ReadUInt32LittleEndian(entry) & 0x0FFFFFFF) == 0) free++;
            else inUse++;
        }

        return new ClusterRangeAssessment(clusters, free, inUse, unknown);
    }

    public byte[] ReadContiguous(uint firstCluster, long length)
    {
        if (firstCluster < 2 || length <= 0) return [];

        // The length came off a directory entry on a damaged volume, so it is exactly
        // as trustworthy as the damage allows. Allocating it unbounded turns one
        // corrupt size field into an OutOfMemoryException that takes the carve - and
        // on a big enough value, the process - down with it. Refusing a length no
        // device could hold costs one comparison.
        if (!RawFileSystem.IsPlausibleLength(length, _sectors.DeviceLength)) return [];

        var buffer = new byte[length];
        var clusterSize = _boot.BytesPerCluster;
        var written = 0;

        for (var cluster = firstCluster; written < length; cluster++)
        {
            var wanted = (int)Math.Min(clusterSize, length - written);
            var offset = _boot.ClusterToOffset(cluster);

            // Stop at the first unreadable cluster and return what was recovered.
            // A short result is honest; padding it with zeroes would hide where the
            // recovery actually ended.
            if (!_sectors.TryRead(offset, buffer.AsSpan(written, wanted)))
                return buffer[..written];

            written += wanted;
        }

        return buffer;
    }

    public static bool TryOpen(Stream stream, out Fat32Reader? reader, out string? error)
    {
        reader = null;

        var sector = new byte[512];
        stream.Seek(0, SeekOrigin.Begin);

        if (stream.ReadAtLeast(sector, 512, throwOnEndOfStream: false) < 512)
        {
            error = "Could not read the boot sector.";
            return false;
        }

        if (!Fat32BootSector.TryParse(sector, out var boot, out error)) return false;

        reader = new Fat32Reader(stream, boot!);
        return true;
    }

    /// <summary>Follows a cluster chain through the FAT.</summary>
    public IReadOnlyList<uint> GetChain(uint firstCluster)
    {
        var chain = new List<uint>();
        if (firstCluster < 2) return chain;

        var seen = new HashSet<uint>();
        var cluster = firstCluster;
        Span<byte> entry = stackalloc byte[4];

        while (cluster >= 2 && cluster < EndOfChain && cluster != BadCluster)
        {
            // A chain that revisits a cluster is corrupt. Stopping keeps the walk
            // finite and preserves what was read up to that point.
            if (!seen.Add(cluster) || chain.Count >= MaxClustersPerChain) break;

            chain.Add(cluster);

            var offset = _boot.FatOffset + ((long)cluster * 4);
            if (!_sectors.TryRead(offset, entry)) break;

            cluster = BinaryPrimitives.ReadUInt32LittleEndian(entry) & 0x0FFFFFFF;
        }

        return chain;
    }

    public byte[] ReadClusterChain(uint firstCluster)
    {
        var chain = GetChain(firstCluster);
        var buffer = new byte[chain.Count * _boot.BytesPerCluster];

        for (var i = 0; i < chain.Count; i++)
        {
            var offset = _boot.ClusterToOffset(chain[i]);
            var destination = buffer.AsSpan(i * _boot.BytesPerCluster, _boot.BytesPerCluster);

            // An unreadable cluster leaves its span zeroed, which the directory
            // parser treats as end-of-directory rather than as garbage entries.
            if (!_sectors.TryRead(offset, destination)) break;
        }

        return buffer;
    }

    public IReadOnlyList<FatDirectoryEntry> ReadDirectory(uint cluster, bool includeDeleted = true) =>
        FatDirectoryParser.Parse(ReadClusterChain(cluster), includeDeleted);

    public IReadOnlyList<FatDirectoryEntry> ReadRootDirectory(bool includeDeleted = true) =>
        ReadDirectory(_boot.RootCluster, includeDeleted);

    /// <summary>
    /// Walks the whole tree, reporting every entry including deleted ones.
    /// </summary>
    public IEnumerable<RawEntry> EnumerateTree(bool includeDeleted = true, int maxDepth = 64)
    {
        var visitedClusters = new HashSet<uint>();
        var pending = new Stack<(uint Cluster, string Path, int Depth)>();
        pending.Push((_boot.RootCluster, string.Empty, 0));

        while (pending.Count > 0)
        {
            var (cluster, path, depth) = pending.Pop();

            // Directories on a damaged volume can point at each other. Without
            // this the walk never terminates.
            if (!visitedClusters.Add(cluster)) continue;

            foreach (var entry in ReadDirectory(cluster, includeDeleted))
            {
                if (entry.IsVolumeLabel) continue;
                if (entry.ShortName is "." or ".." or "_." or "_..") continue;

                var childPath = path.Length == 0 ? entry.Name : $"{path}\\{entry.Name}";

                yield return new RawEntry(
                    childPath, entry.Name, entry.IsDirectory, entry.IsDeleted,
                    entry.FirstCluster, entry.Length);

                // A deleted directory's contents are no longer reliably reachable:
                // its clusters may already be reallocated, so descending risks
                // reporting another file's data under this name.
                if (entry.IsDirectory && !entry.IsDeleted &&
                    entry.FirstCluster >= 2 && depth < maxDepth)
                {
                    pending.Push((entry.FirstCluster, childPath, depth + 1));
                }
            }
        }
    }
}
