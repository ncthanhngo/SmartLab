using System.Buffers.Binary;
using System.Text;

namespace SmartLab.Fat;

[Flags]
public enum FatAttributes : byte
{
    None = 0,
    ReadOnly = 0x01,
    Hidden = 0x02,
    System = 0x04,
    VolumeLabel = 0x08,
    Directory = 0x10,
    Archive = 0x20,

    /// <summary>The marker for a long-file-name fragment, not a real attribute set.</summary>
    LongName = ReadOnly | Hidden | System | VolumeLabel,
}

/// <summary>A parsed 32-byte FAT directory entry.</summary>
public sealed record FatDirectoryEntry
{
    public required string ShortName { get; init; }

    /// <summary>The long name if the entry had one, otherwise the 8.3 name.</summary>
    public required string Name { get; init; }

    public required FatAttributes Attributes { get; init; }
    public required uint FirstCluster { get; init; }
    public required uint Length { get; init; }

    /// <summary>
    /// True when the entry is marked deleted (first byte 0xE5).
    /// </summary>
    /// <remarks>
    /// These are the entries a mounted filesystem will never show and that
    /// <c>chkdsk /F</c> discards. Reading them is the whole reason for going to
    /// raw sectors: the name and starting cluster usually survive deletion, which
    /// is often enough to recover the file.
    /// </remarks>
    public required bool IsDeleted { get; init; }

    public bool IsDirectory => Attributes.HasFlag(FatAttributes.Directory);
    public bool IsVolumeLabel => Attributes.HasFlag(FatAttributes.VolumeLabel);
}

/// <summary>Parses a directory region into entries, reassembling long names.</summary>
public static class FatDirectoryParser
{
    public const int EntrySize = 32;

    private const byte EndOfDirectory = 0x00;
    private const byte DeletedMarker = 0xE5;

    /// <summary>
    /// Substituted for the first character of a deleted entry, whose real value
    /// the 0xE5 marker overwrote and which cannot be recovered from the entry.
    /// </summary>
    private const char LostFirstCharacter = '_';

    public static IReadOnlyList<FatDirectoryEntry> Parse(ReadOnlySpan<byte> directory, bool includeDeleted = true)
    {
        var entries = new List<FatDirectoryEntry>();
        var longNameParts = new SortedDictionary<int, string>();

        for (var offset = 0; offset + EntrySize <= directory.Length; offset += EntrySize)
        {
            var raw = directory.Slice(offset, EntrySize);
            var first = raw[0];

            if (first == EndOfDirectory) break;

            var attributes = (FatAttributes)raw[11];

            if (attributes == FatAttributes.LongName)
            {
                // A long-name fragment. Deleted ones are skipped: their sequence
                // numbers are unreliable and splicing them into a live name would
                // fabricate a filename that never existed.
                if (first != DeletedMarker)
                {
                    var sequence = first & 0x1F;
                    longNameParts[sequence] = ReadLongNameChars(raw);
                }
                continue;
            }

            var deleted = first == DeletedMarker;

            if (deleted && !includeDeleted)
            {
                longNameParts.Clear();
                continue;
            }

            var shortName = ReadShortName(raw, deleted);

            // Long-name fragments precede their entry and are stored in reverse,
            // so ascending sequence order reconstructs the name.
            var longName = longNameParts.Count > 0
                ? string.Concat(longNameParts.Values).TrimEnd('￿', '\0')
                : null;

            longNameParts.Clear();

            entries.Add(new FatDirectoryEntry
            {
                ShortName = shortName,
                Name = string.IsNullOrEmpty(longName) ? shortName : longName,
                Attributes = attributes,
                FirstCluster = ((uint)BinaryPrimitives.ReadUInt16LittleEndian(raw[20..]) << 16) |
                               BinaryPrimitives.ReadUInt16LittleEndian(raw[26..]),
                Length = BinaryPrimitives.ReadUInt32LittleEndian(raw[28..]),
                IsDeleted = deleted,
            });
        }

        return entries;
    }

    private static string ReadLongNameChars(ReadOnlySpan<byte> raw)
    {
        var sb = new StringBuilder(13);
        AppendChars(sb, raw[1..11]);   // characters 1-5
        AppendChars(sb, raw[14..26]);  // characters 6-11
        AppendChars(sb, raw[28..32]);  // characters 12-13
        return sb.ToString();

        static void AppendChars(StringBuilder sb, ReadOnlySpan<byte> span)
        {
            for (var i = 0; i + 1 < span.Length; i += 2)
            {
                var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(span[i..]);
                if (c is '\0' or '￿') return;
                sb.Append(c);
            }
        }
    }

    private static string ReadShortName(ReadOnlySpan<byte> raw, bool deleted)
    {
        // Code page 437 is the historical encoding for 8.3 names. Latin1 keeps
        // every byte distinguishable, which matters when the bytes are garbage and
        // the operator needs to see exactly what is there.
        var name = Encoding.Latin1.GetString(raw[..8]).TrimEnd();
        var extension = Encoding.Latin1.GetString(raw[8..11]).TrimEnd();

        if (deleted && name.Length > 0)
            name = LostFirstCharacter + name[1..];

        return extension.Length > 0 ? $"{name}.{extension}" : name;
    }
}
