using System.Buffers.Binary;
using System.Text;

namespace UsbDoctor.Tests;

/// <summary>
/// Builds a small, valid exFAT image in memory.
/// </summary>
/// <remarks>
/// exFAT describes a file with a set of consecutive 32-byte entries: a File entry,
/// a Stream Extension holding the location and length, and File Name entries
/// carrying the name in 15-character pieces. Deletion only clears the high bit of
/// each entry type, so this builder can produce a deleted file whose name and
/// location are fully intact - the case that makes recovery worth attempting.
/// </remarks>
public sealed class ExFatImageBuilder
{
    public const int BytesPerSector = 512;
    public const int SectorsPerCluster = 1;
    private const uint FatOffsetSectors = 8;
    private const uint FatLengthSectors = 8;
    private const uint ClusterHeapOffsetSectors = 32;
    private const uint ClusterCount = 200;
    public const uint RootCluster = 2;
    private const int TotalSectors = 256;
    private const int EntrySize = 32;

    private readonly byte[] _image = new byte[TotalSectors * BytesPerSector];
    private readonly Dictionary<uint, List<byte[]>> _directories = [];

    public ExFatImageBuilder() => WriteBootSector();

    private static long ClusterOffset(uint cluster) =>
        (ClusterHeapOffsetSectors + ((long)(cluster - 2) * SectorsPerCluster)) * BytesPerSector;

    private void WriteBootSector()
    {
        var s = _image.AsSpan();

        s[0] = 0xEB; s[1] = 0x76; s[2] = 0x90;
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(s[3..]);

        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], FatOffsetSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], FatLengthSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[88..], ClusterHeapOffsetSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[92..], ClusterCount);
        BinaryPrimitives.WriteUInt32LittleEndian(s[96..], RootCluster);

        s[108] = 9;  // 2^9  = 512 bytes per sector
        s[109] = 0;  // 2^0  = 1 sector per cluster
        s[110] = 1;  // one FAT

        s[510] = 0x55; s[511] = 0xAA;
    }

    public ExFatImageBuilder WithBootSectorByte(int offset, byte value)
    {
        _image[offset] = value;
        return this;
    }

    public ExFatImageBuilder SetFatEntry(uint cluster, uint next)
    {
        var offset = (int)((FatOffsetSectors * BytesPerSector) + (cluster * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(_image.AsSpan(offset), next);
        return this;
    }

    public ExFatImageBuilder EndChain(uint cluster) => SetFatEntry(cluster, 0xFFFFFFFF);

    public ExFatImageBuilder AddEntry(
        uint directoryCluster, string name, bool isDirectory,
        uint firstCluster, long length, bool deleted = false, bool contiguous = true)
    {
        var nameEntries = (name.Length + 14) / 15;
        var secondaryCount = (byte)(1 + nameEntries);

        var file = new byte[EntrySize];
        file[0] = deleted ? (byte)0x05 : (byte)0x85;
        file[1] = secondaryCount;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), (ushort)(isDirectory ? 0x10 : 0x20));
        Add(directoryCluster, file);

        var stream = new byte[EntrySize];
        stream[0] = deleted ? (byte)0x40 : (byte)0xC0;
        stream[1] = (byte)(contiguous ? 0x03 : 0x01);
        stream[3] = (byte)name.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(20), firstCluster);
        BinaryPrimitives.WriteInt64LittleEndian(stream.AsSpan(24), length);
        Add(directoryCluster, stream);

        for (var i = 0; i < nameEntries; i++)
        {
            var chunk = name.Substring(i * 15, Math.Min(15, name.Length - (i * 15)));
            var entry = new byte[EntrySize];
            entry[0] = deleted ? (byte)0x41 : (byte)0xC1;

            for (var c = 0; c < chunk.Length; c++)
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2 + (c * 2)), chunk[c]);

            Add(directoryCluster, entry);
        }

        return this;
    }

    /// <summary>Writes file content into consecutive clusters.</summary>
    public ExFatImageBuilder WriteData(uint firstCluster, byte[] data)
    {
        data.CopyTo(_image.AsSpan((int)ClusterOffset(firstCluster)));
        return this;
    }

    private void Add(uint directoryCluster, byte[] entry)
    {
        if (!_directories.TryGetValue(directoryCluster, out var list))
            _directories[directoryCluster] = list = [];

        list.Add(entry);
    }

    public byte[] Build()
    {
        foreach (var (cluster, entries) in _directories)
        {
            var offset = (int)ClusterOffset(cluster);
            for (var i = 0; i < entries.Count; i++)
                entries[i].CopyTo(_image.AsSpan(offset + (i * EntrySize)));
        }

        return _image;
    }

    public MemoryStream BuildStream() => new(Build(), writable: false);

    public SectorAlignedOnlyStream BuildDeviceStream() => new(Build(), BytesPerSector);
}
