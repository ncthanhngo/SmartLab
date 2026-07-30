using UsbDoctor.Core.Paths;

namespace UsbDoctor.Core.Model;

[Flags]
public enum EntryAttributes : uint
{
    None      = 0,
    ReadOnly  = 0x1,
    Hidden    = 0x2,
    System    = 0x4,
    Directory = 0x10,
    Archive   = 0x20,
    Normal    = 0x80,
    Temporary = 0x100,
    ReparsePoint = 0x400,
}

/// <summary>A directory entry that was read successfully.</summary>
public sealed record FileEntry(
    ExtendedPath Path,
    string Name,
    long Length,
    EntryAttributes Attributes,
    DateTimeOffset? LastWriteUtc)
{
    public bool IsDirectory => Attributes.HasFlag(EntryAttributes.Directory);
    public bool IsHidden    => Attributes.HasFlag(EntryAttributes.Hidden);
    public bool IsSystem    => Attributes.HasFlag(EntryAttributes.System);

    /// <summary>True for the <c>.</c> and <c>..</c> pseudo-entries.</summary>
    public bool IsDotEntry => Name is "." or "..";
}

/// <summary>
/// The outcome of reading one directory entry.
/// </summary>
/// <remarks>
/// Enumeration is modelled per-entry rather than per-directory on purpose. On the
/// volume this tool was built for, a single corrupt entry inside a
/// <c>*.sd</c> folder caused <c>Directory.EnumerateFileSystemEntries</c> to throw
/// and discard the entire directory — including the readable siblings. A damaged
/// entry must degrade to one <see cref="Damaged"/> record, never to a lost
/// directory.
/// </remarks>
public abstract record EnumEntry
{
    public sealed record Ok(FileEntry Entry) : EnumEntry;

    public sealed record Damaged(ExtendedPath Parent, string? RawName, int Win32Error, string Message)
        : EnumEntry;
}
