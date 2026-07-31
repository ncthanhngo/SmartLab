using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SmartLab.Core.Abstractions;
using SmartLab.Core.Model;
using SmartLab.Core.Paths;
using SmartLab.Win32.Native;
using static SmartLab.Win32.Native.NativeMethods;

namespace SmartLab.Win32.Io;

/// <summary>
/// Reads a mounted volume through the raw Win32 find APIs.
/// </summary>
/// <remarks>
/// This deliberately does not use <see cref="Directory.EnumerateFileSystemEntries(string)"/>.
/// That helper throws on the first unreadable entry and discards the whole
/// directory with it. On a volume with corrupt FAT entries — names built from
/// arbitrary bytes, impossible sizes, invalid timestamps — that behaviour loses
/// every readable sibling of the first bad entry.
/// </remarks>
public sealed class Win32VolumeReader : IVolumeReader
{
    public async IAsyncEnumerable<EnumEntry> EnumerateAsync(
        ExtendedPath directory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();

        foreach (var entry in EnumerateCore(directory, ct))
            yield return entry;
    }

    private static IEnumerable<EnumEntry> EnumerateCore(ExtendedPath directory, CancellationToken ct)
    {
        var pattern = directory.Value.TrimEnd('\\') + @"\*";

        using var handle = FindFirstFileExW(
            pattern,
            FINDEX_INFO_LEVELS.Basic,
            out var data,
            FINDEX_SEARCH_OPS.NameMatch,
            IntPtr.Zero,
            0);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();

            // An empty directory is not a fault.
            if (error is not (ERROR_NO_MORE_FILES or ERROR_FILE_NOT_FOUND))
                yield return new EnumEntry.Damaged(directory, null, error, DescribeError(error));

            yield break;
        }

        do
        {
            ct.ThrowIfCancellationRequested();

            EnumEntry item;
            try
            {
                item = new EnumEntry.Ok(ToEntry(directory, in data));
            }
            catch (Exception ex)
            {
                // One malformed entry must cost exactly one entry.
                item = new EnumEntry.Damaged(directory, SafeName(in data), 0, ex.Message);
            }

            if (item is EnumEntry.Ok ok && ok.Entry.IsDotEntry)
                continue;

            yield return item;
        }
        while (FindNextFileW(handle, out data));

        var last = Marshal.GetLastWin32Error();
        if (last is not ERROR_NO_MORE_FILES)
            yield return new EnumEntry.Damaged(directory, null, last, DescribeError(last));
    }

    private static string? SafeName(in WIN32_FIND_DATAW data)
    {
        try { return data.cFileName; }
        catch { return null; }
    }

    private static FileEntry ToEntry(ExtendedPath parent, in WIN32_FIND_DATAW data)
    {
        var name = data.cFileName;

        return new FileEntry(
            Path: parent.Child(name),
            Name: name,
            Length: data.FileSize,
            Attributes: (EntryAttributes)data.dwFileAttributes,
            LastWriteUtc: ToDateTime(data.ftLastWriteTime));
    }

    /// <summary>
    /// Converts a FILETIME, tolerating the invalid values that damaged
    /// directory entries carry.
    /// </summary>
    /// <remarks>
    /// chkdsk reported "Invalid time stamp" against real files on the source
    /// volume. <see cref="DateTimeOffset.FromFileTime"/> throws on those, so an
    /// unguarded conversion would turn a cosmetic metadata fault into a lost
    /// file.
    /// </remarks>
    private static DateTimeOffset? ToDateTime(FILETIME ft)
    {
        var ticks = ft.ToTicks();
        if (ticks <= 0) return null;

        try
        {
            return DateTimeOffset.FromFileTime(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public Stream OpenRead(ExtendedPath file) =>
        new FileStream(file.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16,
            FileOptions.SequentialScan);

    public VolumeInfo? GetVolume(char driveLetter)
    {
        var root = $"{char.ToUpperInvariant(driveLetter)}:\\";

        var driveType = GetDriveTypeW(root) switch
        {
            2 => VolumeDriveType.Removable,
            3 => VolumeDriveType.Fixed,
            4 => VolumeDriveType.Network,
            5 => VolumeDriveType.CdRom,
            6 => VolumeDriveType.RamDisk,
            _ => VolumeDriveType.Unknown,
        };

        if (driveType == VolumeDriveType.Unknown)
            return null;

        var label = new StringBuilder(261);
        var fsName = new StringBuilder(261);

        var haveInfo = GetVolumeInformationW(
            root, label, label.Capacity, out _, out _, out _, fsName, fsName.Capacity);

        if (!GetDiskFreeSpaceExW(root, out _, out var total, out var free))
            return null;

        return new VolumeInfo(
            DriveLetter: char.ToUpperInvariant(driveLetter),
            Label: haveInfo && label.Length > 0 ? label.ToString() : null,
            FileSystem: haveInfo && fsName.Length > 0 ? fsName.ToString() : null,
            SizeBytes: (long)total,
            FreeBytes: (long)free,
            DriveType: driveType);
    }

    internal static string DescribeError(int error) => error switch
    {
        ERROR_ACCESS_DENIED => "Access denied.",
        ERROR_NOT_READY => "Device not ready — the volume may have dropped off the bus.",
        ERROR_INVALID_NAME => "Invalid name — the entry holds bytes no filesystem will accept.",
        ERROR_DIRECTORY_INVALID => "Directory name is invalid.",
        ERROR_FILE_CORRUPT => "The file or directory is corrupted and unreadable.",
        _ => new Win32Exception(error).Message,
    };
}
