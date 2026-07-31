using System.Buffers.Binary;
using System.Text;

namespace SmartLab.Fat;

/// <summary>
/// Reads exFAT structures directly from a byte stream.
/// </summary>
/// <remarks>
/// exFAT stores a file as a set of consecutive 32-byte entries rather than one:
/// a File entry carrying the attributes, a Stream Extension carrying the starting
/// cluster and length, and one or more File Name entries holding the name in
/// 15-character pieces. A deleted file keeps all of them, with only the high bit
/// of each entry type cleared, so the name and location survive deletion intact -
/// noticeably better than FAT32, where the first character of the short name is
/// overwritten.
/// </remarks>
public sealed class ExFatReader : IRawFileSystem
{
    private readonly SectorReader _sectors;
    private readonly ExFatBootSector _boot;

    private const byte EntryTypeAllocationBitmap = 0x81;
    private const byte EntryTypeFile = 0x85;
    private const byte EntryTypeStream = 0xC0;
    private const byte EntryTypeFileName = 0xC1;
    private const byte InUseFlag = 0x80;

    private const uint EndOfChain = 0xFFFFFFF8;
    private const int EntrySize = 32;
    private const int MaxClustersPerChain = 1 << 20;

    private ExFatReader(Stream stream, ExFatBootSector boot)
    {
        _boot = boot;
        _sectors = new SectorReader(stream, boot.BytesPerSector);
    }

    public ExFatBootSector BootSector => _boot;

    public int BytesPerCluster => _boot.BytesPerCluster;

    public string Describe() =>
        $"exFAT  {_boot.BytesPerSector} B/sector  {_boot.SectorsPerCluster} sector(s)/cluster  " +
        $"{_boot.BytesPerCluster} B/cluster  root cluster {_boot.RootDirectoryCluster}";

    public static bool TryOpen(Stream stream, out ExFatReader? reader, out string? error)
    {
        reader = null;

        var sector = new byte[512];
        stream.Seek(0, SeekOrigin.Begin);

        if (stream.ReadAtLeast(sector, 512, throwOnEndOfStream: false) < 512)
        {
            error = "Could not read the boot sector.";
            return false;
        }

        if (!ExFatBootSector.TryParse(sector, out var boot, out error)) return false;

        reader = new ExFatReader(stream, boot!);
        return true;
    }

    public IReadOnlyList<uint> GetChain(uint firstCluster)
    {
        var chain = new List<uint>();
        if (firstCluster < 2) return chain;

        var seen = new HashSet<uint>();
        var cluster = firstCluster;
        Span<byte> entry = stackalloc byte[4];

        while (cluster >= 2 && cluster < EndOfChain)
        {
            if (!seen.Add(cluster) || chain.Count >= MaxClustersPerChain) break;

            chain.Add(cluster);

            if (!_sectors.TryRead(_boot.FatOffset + ((long)cluster * 4), entry)) break;

            cluster = BinaryPrimitives.ReadUInt32LittleEndian(entry);
        }

        return chain;
    }

    private bool _bitmapSearched;
    private IReadOnlyList<uint>? _bitmapChain;
    private long _bitmapLength;

    /// <summary>
    /// Locates the allocation bitmap, which exFAT stores as an ordinary entry in
    /// the root directory rather than at a fixed offset.
    /// </summary>
    private bool TryLoadBitmap()
    {
        if (_bitmapSearched) return _bitmapChain is { Count: > 0 };
        _bitmapSearched = true;

        var root = ReadDirectoryData(_boot.RootDirectoryCluster, contiguous: false);

        for (var offset = 0; offset + EntrySize <= root.Length; offset += EntrySize)
        {
            var type = root[offset];
            if (type == 0x00) break;
            if (type != EntryTypeAllocationBitmap) continue;

            // Bit 0 of the flags selects which FAT the bitmap describes. On a
            // single-FAT volume only the first is meaningful.
            if ((root[offset + 1] & 0x01) != 0) continue;

            var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(offset + 20));
            _bitmapLength = BinaryPrimitives.ReadInt64LittleEndian(root.AsSpan(offset + 24));

            if (firstCluster < 2 || _bitmapLength <= 0) return false;

            _bitmapChain = GetChain(firstCluster);
            return _bitmapChain.Count > 0;
        }

        return false;
    }

    /// <summary>Reads one byte of the bitmap, mapping through its cluster chain.</summary>
    private bool TryReadBitmapByte(long byteOffset, out byte value)
    {
        value = 0;

        if (_bitmapChain is null || byteOffset < 0 || byteOffset >= _bitmapLength) return false;

        var index = (int)(byteOffset / _boot.BytesPerCluster);
        if (index >= _bitmapChain.Count) return false;

        var within = byteOffset % _boot.BytesPerCluster;
        var offset = _boot.ClusterToOffset(_bitmapChain[index]) + within;

        Span<byte> one = stackalloc byte[1];
        if (!_sectors.TryRead(offset, one)) return false;

        value = one[0];
        return true;
    }

    /// <summary>
    /// Assesses a carve range against the allocation bitmap.
    /// </summary>
    /// <remarks>
    /// Each bit covers one cluster starting at cluster 2; a set bit means the
    /// cluster belongs to a live file. Reading a set cluster back returns that
    /// file's bytes, not the deleted one's.
    /// </remarks>
    public ClusterRangeAssessment AssessRange(uint firstCluster, long length)
    {
        if (firstCluster < 2 || length <= 0) return ClusterRangeAssessment.None;

        var clusters = (int)Math.Min(
            MaxClustersPerChain,
            (length + _boot.BytesPerCluster - 1) / _boot.BytesPerCluster);

        if (!TryLoadBitmap()) return new ClusterRangeAssessment(clusters, 0, 0, clusters);

        int free = 0, inUse = 0, unknown = 0;

        for (var i = 0; i < clusters; i++)
        {
            var bit = (long)firstCluster + i - 2;

            if (!TryReadBitmapByte(bit / 8, out var b)) unknown++;
            else if ((b & (1 << (int)(bit % 8))) != 0) inUse++;
            else free++;
        }

        return new ClusterRangeAssessment(clusters, free, inUse, unknown);
    }

    public byte[] ReadContiguous(uint firstCluster, long length)
    {
        if (firstCluster < 2 || length <= 0) return [];

        // exFAT stores a 64-bit size, so a corrupt entry can declare more bytes than
        // exist on any device. See the note in Fat32Reader: an untrusted length must
        // never become an unbounded allocation.
        if (!RawFileSystem.IsPlausibleLength(length, _sectors.DeviceLength)) return [];

        var buffer = new byte[length];
        var written = 0;

        for (var cluster = firstCluster; written < length; cluster++)
        {
            var wanted = (int)Math.Min(_boot.BytesPerCluster, length - written);

            if (!_sectors.TryRead(_boot.ClusterToOffset(cluster), buffer.AsSpan(written, wanted)))
                return buffer[..written];

            written += wanted;
        }

        return buffer;
    }

    private byte[] ReadDirectoryData(uint firstCluster, bool contiguous)
    {
        if (contiguous)
        {
            // A directory declared contiguous has no FAT chain to follow. Its true
            // extent is unknown here, so read one cluster and let the entry parser
            // stop at the end-of-directory marker.
            return ReadContiguous(firstCluster, _boot.BytesPerCluster);
        }

        var chain = GetChain(firstCluster);
        var buffer = new byte[chain.Count * _boot.BytesPerCluster];

        for (var i = 0; i < chain.Count; i++)
        {
            var destination = buffer.AsSpan(i * _boot.BytesPerCluster, _boot.BytesPerCluster);
            if (!_sectors.TryRead(_boot.ClusterToOffset(chain[i]), destination)) break;
        }

        return buffer;
    }

    /// <summary>Parses one directory's entry set.</summary>
    public IReadOnlyList<RawEntry> ReadDirectory(
        uint cluster, string parentPath, bool contiguous, bool includeDeleted)
    {
        var data = ReadDirectoryData(cluster, contiguous);
        var results = new List<RawEntry>();

        for (var offset = 0; offset + EntrySize <= data.Length; offset += EntrySize)
        {
            var type = data[offset];
            if (type == 0x00) break; // end of directory

            var inUse = (type & InUseFlag) != 0;
            var baseType = (byte)(type | InUseFlag);

            if (baseType != EntryTypeFile) continue;
            if (!inUse && !includeDeleted) continue;

            // The File entry is followed by its Stream Extension and File Name
            // entries. Without the stream entry there is no location or length, so
            // the set is unusable.
            var secondaryCount = data[offset + 1];
            var streamOffset = offset + EntrySize;
            if (streamOffset + EntrySize > data.Length) break;

            if ((data[streamOffset] | InUseFlag) != EntryTypeStream) continue;

            var attributes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
            var isDirectory = (attributes & 0x10) != 0;

            var secondaryFlags = data[streamOffset + 1];
            var isContiguous = (secondaryFlags & 0x02) != 0;

            var nameLength = data[streamOffset + 3];
            var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(streamOffset + 20));
            var dataLength = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(streamOffset + 24));

            var name = ReadName(data, streamOffset + EntrySize, secondaryCount - 1, nameLength);
            if (string.IsNullOrEmpty(name)) name = $"cluster{firstCluster}";

            var path = parentPath.Length == 0 ? name : $"{parentPath}\\{name}";

            results.Add(new RawEntry(
                path, name, isDirectory, !inUse, firstCluster, dataLength, isContiguous));

            offset += secondaryCount * EntrySize;
        }

        return results;
    }

    private static string ReadName(byte[] data, int offset, int nameEntryCount, int nameLength)
    {
        var sb = new StringBuilder(nameLength);

        for (var i = 0; i < nameEntryCount && sb.Length < nameLength; i++)
        {
            var entry = offset + (i * EntrySize);
            if (entry + EntrySize > data.Length) break;
            if ((data[entry] | InUseFlag) != EntryTypeFileName) continue;

            for (var c = 0; c < 15 && sb.Length < nameLength; c++)
            {
                var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(entry + 2 + (c * 2)));
                if (value == 0) break;
                sb.Append((char)value);
            }
        }

        return sb.ToString();
    }

    public IEnumerable<RawEntry> EnumerateTree(bool includeDeleted = true, int maxDepth = 64)
    {
        var visited = new HashSet<uint>();
        var pending = new Stack<(uint Cluster, string Path, bool Contiguous, int Depth)>();
        pending.Push((_boot.RootDirectoryCluster, string.Empty, false, 0));

        while (pending.Count > 0)
        {
            var (cluster, path, contiguous, depth) = pending.Pop();
            if (!visited.Add(cluster)) continue;

            foreach (var entry in ReadDirectory(cluster, path, contiguous, includeDeleted))
            {
                yield return entry;

                // A deleted directory's clusters may already have been reused, so
                // descending would report another file's data under this name.
                if (entry.IsDirectory && !entry.IsDeleted &&
                    entry.FirstCluster >= 2 && depth < maxDepth)
                {
                    pending.Push((entry.FirstCluster, entry.Path, entry.IsContiguous, depth + 1));
                }
            }
        }
    }
}
