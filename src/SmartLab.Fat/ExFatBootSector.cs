using System.Buffers.Binary;
using System.Text;

namespace SmartLab.Fat;

/// <summary>
/// The exFAT Main Boot Record.
/// </summary>
/// <remarks>
/// exFAT states geometry as power-of-two shifts rather than direct counts, so the
/// shift values are validated before use: an out-of-range shift would produce a
/// nonsensical cluster size and send every later offset somewhere arbitrary on the
/// device.
/// </remarks>
public sealed record ExFatBootSector
{
    public required uint FatOffsetSectors { get; init; }
    public required uint FatLengthSectors { get; init; }
    public required uint ClusterHeapOffsetSectors { get; init; }
    public required uint ClusterCount { get; init; }
    public required uint RootDirectoryCluster { get; init; }
    public required int BytesPerSector { get; init; }
    public required int SectorsPerCluster { get; init; }
    public required byte NumberOfFats { get; init; }

    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;

    public long FatOffset => (long)FatOffsetSectors * BytesPerSector;

    public long ClusterToOffset(uint cluster) =>
        ((long)ClusterHeapOffsetSectors + ((long)(cluster - 2) * SectorsPerCluster)) * BytesPerSector;

    public static bool TryParse(ReadOnlySpan<byte> sector, out ExFatBootSector? result, out string? error)
    {
        result = null;
        error = null;

        if (sector.Length < 512)
        {
            error = "Boot sector is shorter than 512 bytes.";
            return false;
        }

        if (!Encoding.ASCII.GetString(sector[3..11]).StartsWith("EXFAT", StringComparison.Ordinal))
        {
            error = "File system name is not EXFAT.";
            return false;
        }

        var bytesPerSectorShift = sector[108];
        var sectorsPerClusterShift = sector[109];

        // 2^9 = 512 up to 2^12 = 4096 bytes per sector; a cluster may not exceed
        // 32 MB, which caps the combined shift at 25.
        if (bytesPerSectorShift is < 9 or > 12)
        {
            error = $"Implausible bytes-per-sector shift: {bytesPerSectorShift}.";
            return false;
        }

        if (sectorsPerClusterShift > 25 - bytesPerSectorShift)
        {
            error = $"Implausible sectors-per-cluster shift: {sectorsPerClusterShift}.";
            return false;
        }

        var fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(sector[80..]);
        var fatLength = BinaryPrimitives.ReadUInt32LittleEndian(sector[84..]);
        var heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(sector[88..]);
        var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(sector[92..]);
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(sector[96..]);
        var fats = sector[110];

        if (fatOffset == 0 || fatLength == 0 || heapOffset == 0)
        {
            error = "FAT offset, FAT length, or cluster heap offset is zero.";
            return false;
        }

        if (rootCluster < 2 || (clusterCount != 0 && rootCluster > clusterCount + 1))
        {
            error = $"Root directory cluster {rootCluster} is outside the cluster heap.";
            return false;
        }

        result = new ExFatBootSector
        {
            FatOffsetSectors = fatOffset,
            FatLengthSectors = fatLength,
            ClusterHeapOffsetSectors = heapOffset,
            ClusterCount = clusterCount,
            RootDirectoryCluster = rootCluster,
            BytesPerSector = 1 << bytesPerSectorShift,
            SectorsPerCluster = 1 << sectorsPerClusterShift,
            NumberOfFats = fats == 0 ? (byte)1 : fats,
        };

        return true;
    }
}
