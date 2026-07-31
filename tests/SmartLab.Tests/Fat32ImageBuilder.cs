using System.Buffers.Binary;
using System.Text;
using SmartLab.Fat;

namespace SmartLab.Tests;

/// <summary>
/// Builds a small, valid FAT32 image in memory.
/// </summary>
/// <remarks>
/// This replaces the VHD-based fixtures originally planned. Mounting a VHD needs
/// Administrator and Hyper-V, which makes tests unrunnable on a developer
/// workstation and in CI. Writing the same structures into a byte array gives
/// deterministic, reviewable fixtures with no privileges at all — and lets a test
/// construct damage that would be near impossible to produce on purpose, such as
/// a directory entry whose name is arbitrary bytes or a cluster chain that points
/// back at itself.
/// </remarks>
public sealed class Fat32ImageBuilder
{
    public const ushort BytesPerSector = 512;
    public const byte SectorsPerCluster = 1;
    public const ushort ReservedSectors = 32;
    public const byte NumberOfFats = 1;
    public const uint SectorsPerFat = 32;
    public const uint RootCluster = 2;
    private const int TotalSectors = 256;

    private readonly byte[] _image = new byte[TotalSectors * BytesPerSector];
    private readonly Dictionary<uint, List<byte[]>> _directories = [];

    public Fat32ImageBuilder() => WriteBootSector();

    private static long FatOffset => (long)ReservedSectors * BytesPerSector;
    private static long FirstDataSector => ReservedSectors + (NumberOfFats * SectorsPerFat);

    private static long ClusterOffset(uint cluster) =>
        (FirstDataSector + (cluster - 2)) * BytesPerSector;

    private void WriteBootSector()
    {
        var s = _image.AsSpan();

        s[0] = 0xEB; s[1] = 0x58; s[2] = 0x90;                       // jump instruction
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(s[3..]);          // OEM name

        BinaryPrimitives.WriteUInt16LittleEndian(s[11..], BytesPerSector);
        s[13] = SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], ReservedSectors);
        s[16] = NumberOfFats;
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], TotalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], SectorsPerFat);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], RootCluster);

        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(s[82..]);

        s[510] = 0x55; s[511] = 0xAA;                                // boot signature
    }

    /// <summary>Corrupts a boot-sector field, for testing validation.</summary>
    public Fat32ImageBuilder WithBootSectorByte(int offset, byte value)
    {
        _image[offset] = value;
        return this;
    }

    public Fat32ImageBuilder SetFatEntry(uint cluster, uint next)
    {
        var offset = (int)(FatOffset + (cluster * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(_image.AsSpan(offset), next);
        return this;
    }

    /// <summary>Marks a cluster as the last in its chain.</summary>
    public Fat32ImageBuilder EndChain(uint cluster) => SetFatEntry(cluster, 0x0FFFFFFF);

    public Fat32ImageBuilder AddFile(
        uint directoryCluster, string shortName, FatAttributes attributes,
        uint firstCluster, uint length, bool deleted = false)
    {
        Add(directoryCluster, ShortEntry(shortName, attributes, firstCluster, length, deleted));
        return this;
    }

    /// <summary>Adds an entry whose 8.3 name is arbitrary bytes, as a corrupt entry has.</summary>
    public Fat32ImageBuilder AddRawNamedFile(uint directoryCluster, byte[] nameBytes, uint firstCluster)
    {
        var entry = new byte[FatDirectoryParser.EntrySize];
        nameBytes.AsSpan(0, Math.Min(11, nameBytes.Length)).CopyTo(entry);
        entry[11] = (byte)FatAttributes.Archive;
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(26), (ushort)firstCluster);

        Add(directoryCluster, entry);
        return this;
    }

    /// <summary>Adds a file with a long name, emitting the LFN fragments before it.</summary>
    public Fat32ImageBuilder AddLongNamedFile(
        uint directoryCluster, string longName, string shortName, uint firstCluster, uint length)
    {
        var chunks = Chunk(longName).ToList();

        // LFN fragments are stored in reverse, with 0x40 flagging the last one.
        for (var i = chunks.Count - 1; i >= 0; i--)
        {
            var sequence = (byte)(i + 1);
            if (i == chunks.Count - 1) sequence |= 0x40;
            Add(directoryCluster, LongNameEntry(sequence, chunks[i]));
        }

        Add(directoryCluster, ShortEntry(shortName, FatAttributes.Archive, firstCluster, length, false));
        return this;
    }

    private static IEnumerable<string> Chunk(string name)
    {
        for (var i = 0; i < name.Length; i += 13)
            yield return name.Substring(i, Math.Min(13, name.Length - i));
    }

    private static byte[] LongNameEntry(byte sequence, string chunk)
    {
        var entry = new byte[FatDirectoryParser.EntrySize];
        entry[0] = sequence;
        entry[11] = (byte)FatAttributes.LongName;

        // Character slots are split across three disjoint ranges of the entry.
        int[] slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];

        for (var i = 0; i < slots.Length; i++)
        {
            var value = i < chunk.Length ? chunk[i] : (i == chunk.Length ? '\0' : '￿');
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(slots[i]), value);
        }

        return entry;
    }

    private static byte[] ShortEntry(
        string shortName, FatAttributes attributes, uint firstCluster, uint length, bool deleted)
    {
        var entry = new byte[FatDirectoryParser.EntrySize];

        var parts = shortName.Split('.');
        var name = parts[0].PadRight(8).ToUpperInvariant();
        var extension = (parts.Length > 1 ? parts[1] : string.Empty).PadRight(3).ToUpperInvariant();

        Encoding.ASCII.GetBytes(name[..8]).CopyTo(entry.AsSpan(0));
        Encoding.ASCII.GetBytes(extension[..3]).CopyTo(entry.AsSpan(8));

        if (deleted) entry[0] = 0xE5;

        entry[11] = (byte)attributes;
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(20), (ushort)(firstCluster >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(26), (ushort)(firstCluster & 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(28), length);

        return entry;
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
                entries[i].CopyTo(_image.AsSpan(offset + (i * FatDirectoryParser.EntrySize)));
        }

        return _image;
    }

    public MemoryStream BuildStream() => new(Build(), writable: false);

    /// <summary>Wraps the image in a stream that only permits sector-aligned reads.</summary>
    public SectorAlignedOnlyStream BuildDeviceStream() => new(Build(), BytesPerSector);
}

/// <summary>
/// A stream that rejects any read which is not sector-aligned, the way a real
/// volume opened as <c>\\.\E:</c> does.
/// </summary>
/// <remarks>
/// A plain <see cref="MemoryStream"/> happily serves a 4-byte read at an arbitrary
/// offset, so FAT parsing code tested only against one passes while being unable
/// to read any actual device — which is exactly what happened: the first run
/// against real hardware failed with ERROR_INVALID_PARAMETER. Running the same
/// tests through this wrapper keeps that mistake from coming back.
/// </remarks>
public sealed class SectorAlignedOnlyStream(byte[] data, int sectorSize) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => data.Length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position % sectorSize != 0)
            throw new IOException("The parameter is incorrect. : unaligned offset");

        if (count % sectorSize != 0)
            throw new IOException("The parameter is incorrect. : unaligned length");

        var available = (int)Math.Min(count, data.Length - _position);
        if (available <= 0) return 0;

        Array.Copy(data, _position, buffer, offset, available);
        _position += available;
        return available;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => data.Length + offset,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
