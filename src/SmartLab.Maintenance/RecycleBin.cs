using System.Runtime.InteropServices;

namespace SmartLab.Maintenance;

/// <summary>One drive's Recycle Bin.</summary>
/// <param name="Root">Drive root, in the form the shell wants: <c>E:\</c>.</param>
/// <param name="IsRemovable">
/// Called out because emptying a removable drive's bin is the more consequential of
/// the two: the drive may not be here tomorrow, and this tool's other half exists to
/// carve those files back.
/// </param>
public sealed record RecycleBinInfo(
    string Root, string? Label, long Bytes, long Items, bool IsRemovable)
{
    public string SizeText => Bytes switch
    {
        0 => "empty",
        < 1024 * 1024 => $"{Bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{Bytes / 1024.0 / 1024:F1} MB",
        _ => $"{Bytes / 1024.0 / 1024 / 1024:F2} GB",
    };
}

/// <summary>
/// Queries and empties the Recycle Bin through the shell.
/// </summary>
/// <remarks>
/// Deleting the <c>$Recycle.Bin</c> folders directly would leave the shell's index
/// pointing at files that no longer exist, so Explorer keeps showing phantom
/// entries and the reported size stays wrong until the index is rebuilt. The shell
/// API is the only way to empty it correctly.
/// </remarks>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [Flags]
    private enum EmptyFlags : uint
    {
        NoConfirmation = 0x1,
        NoProgressUi = 0x2,
        NoSound = 0x4,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, EmptyFlags flags);

    /// <summary>Total bytes across every drive, or 0 if it cannot be read.</summary>
    public static long QuerySize()
    {
        try
        {
            var info = new SHQUERYRBINFO();
            info.cbSize = Marshal.SizeOf<SHQUERYRBINFO>();

            // A null root asks about every drive at once.
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static long QueryItemCount()
    {
        try
        {
            var info = new SHQUERYRBINFO();
            info.cbSize = Marshal.SizeOf<SHQUERYRBINFO>();

            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64NumItems : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Every drive that has a bin, measured separately.
    /// </summary>
    /// <remarks>
    /// Per drive rather than as one total, because they are not one decision. The
    /// system bin holds what a user deleted from their own machine; a bin on a
    /// removable drive holds what was deleted from a stick that may have travelled
    /// between machines, and is exactly the sort of thing this tool exists to look
    /// inside before anyone empties it.
    /// </remarks>
    public static IReadOnlyList<RecycleBinInfo> Enumerate()
    {
        var bins = new List<RecycleBinInfo>();

        foreach (var drive in SafeDrives())
        {
            var root = drive.RootDirectory.FullName;
            if (!TryQuery(root, out var bytes, out var items)) continue;

            // A drive with no bin at all is not a row. An empty bin is, because
            // "nothing here" is an answer the operator came for.
            bins.Add(new RecycleBinInfo(
                root,
                SafeLabel(drive),
                bytes,
                items,
                drive.DriveType == DriveType.Removable));
        }

        return bins;
    }

    /// <remarks>
    /// One unreadable drive must not stop the enumeration. A card reader with no
    /// card, or a device mid-removal, throws on nearly every property.
    /// </remarks>
    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            bool usable;

            try
            {
                usable = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
            }
            catch
            {
                usable = false;
            }

            if (usable) yield return drive;
        }
    }

    private static string? SafeLabel(DriveInfo drive)
    {
        try
        {
            return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryQuery(string? root, out long bytes, out long items)
    {
        bytes = 0;
        items = 0;

        try
        {
            var info = new SHQUERYRBINFO();
            info.cbSize = Marshal.SizeOf<SHQUERYRBINFO>();

            if (SHQueryRecycleBin(root, ref info) != 0) return false;

            bytes = info.i64Size;
            items = info.i64NumItems;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Empties one drive's bin, or every drive when <paramref name="root"/> is null.
    /// </summary>
    /// <remarks>
    /// Confirmation is suppressed here because the caller has already obtained it -
    /// the operator ticked this and pressed a button. A second shell prompt on top
    /// of the app's own would just train people to click through both.
    /// </remarks>
    public static bool Empty(out string? error, string? root = null)
    {
        error = null;

        try
        {
            var result = SHEmptyRecycleBin(
                IntPtr.Zero, root,
                EmptyFlags.NoConfirmation | EmptyFlags.NoProgressUi | EmptyFlags.NoSound);

            // S_OK, or E_UNEXPECTED when it is already empty - both mean there is
            // nothing left to do.
            if (result is 0 or unchecked((int)0x8000FFFF)) return true;

            error = $"Shell returned 0x{result:X8}.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
