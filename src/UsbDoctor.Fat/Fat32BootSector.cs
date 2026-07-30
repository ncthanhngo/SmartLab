using System.Buffers.Binary;
using System.Text;

namespace UsbDoctor.Fat;

/// <summary>
/// The FAT32 BIOS Parameter Block, read straight from sector 0.
/// </summary>
/// <remarks>
/// Everything else in the raw reader is derived from these numbers, so they are
/// validated on construction. A corrupt BPB read as if it were valid would send
/// cluster arithmetic to arbitrary offsets on the device.
/// </remarks>
public sealed record Fat32BootSector
{
    public required ushort BytesPerSector { get; init; }
    public required byte SectorsPerCluster { get; init; }
    public required ushort ReservedSectors { get; init; }
    public required byte NumberOfFats { get; init; }
    public required uint TotalSectors { get; init; }
    public required uint SectorsPerFat { get; init; }
    public required uint RootCluster { get; init; }

    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;

    /// <summary>First sector of the data region, where cluster 2 begins.</summary>
    public uint FirstDataSector => (uint)(ReservedSectors + (NumberOfFats * SectorsPerFat));

    public long FatOffset => (long)ReservedSectors * BytesPerSector;

    public long ClusterToOffset(uint cluster) =>
        ((long)FirstDataSector + ((long)(cluster - 2) * SectorsPerCluster)) * BytesPerSector;

    public static bool TryParse(ReadOnlySpan<byte> sector, out Fat32BootSector? result, out string? error)
    {
        result = null;
        error = null;

        if (sector.Length < 512)
        {
            error = "Boot sector is shorter than 512 bytes.";
            return false;
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
        var sectorsPerCluster = sector[13];
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(sector[14..]);
        var fats = sector[16];
        var totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(sector[32..]);
        var sectorsPerFat32 = BinaryPrimitives.ReadUInt32LittleEndian(sector[36..]);
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(sector[44..]);

        // A sector size that is not a power of two between 512 and 4096, or a
        // cluster size that is not a power of two, means this is not a FAT32 BPB -
        // or the sector is damaged. Either way the geometry cannot be trusted.
        if (bytesPerSector is < 512 or > 4096 || !uint.IsPow2(bytesPerSector))
        {
            error = $"Implausible bytes-per-sector: {bytesPerSector}.";
            return false;
        }

        if (sectorsPerCluster == 0 || !uint.IsPow2(sectorsPerCluster))
        {
            error = $"Implausible sectors-per-cluster: {sectorsPerCluster}.";
            return false;
        }

        if (reserved == 0 || fats == 0 || sectorsPerFat32 == 0)
        {
            error = "Reserved sectors, FAT count, or FAT size is zero.";
            return false;
        }

        if (rootCluster < 2)
        {
            error = $"Root cluster {rootCluster} is below the first data cluster.";
            return false;
        }

        var label = Encoding.ASCII.GetString(sector[82..90]);
        if (!label.StartsWith("FAT32", StringComparison.Ordinal))
        {
            error = $"File system type is '{label.Trim()}', not FAT32.";
            return false;
        }

        result = new Fat32BootSector
        {
            BytesPerSector = bytesPerSector,
            SectorsPerCluster = sectorsPerCluster,
            ReservedSectors = reserved,
            NumberOfFats = fats,
            TotalSectors = totalSectors32,
            SectorsPerFat = sectorsPerFat32,
            RootCluster = rootCluster,
        };

        return true;
    }
}
