using System.Runtime.InteropServices;

namespace UsbDoctor.Maintenance;

/// <summary>
/// Queries and empties the Recycle Bin through the shell.
/// </summary>
/// <remarks>
/// Deleting the <c>$Recycle.Bin</c> folders directly would leave the shell's index
/// pointing at files that no longer exist, so Explorer keeps showing phantom
/// entries and the reported size stays wrong until the index is rebuilt. The shell
/// API is the only way to empty it correctly.
/// </remarks>
internal static class RecycleBin
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
    /// Empties every drive's bin without prompting.
    /// </summary>
    /// <remarks>
    /// Confirmation is suppressed here because the caller has already obtained it -
    /// the operator ticked this and pressed a button. A second shell prompt on top
    /// of the app's own would just train people to click through both.
    /// </remarks>
    public static bool Empty(out string? error)
    {
        error = null;

        try
        {
            var result = SHEmptyRecycleBin(
                IntPtr.Zero, null,
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
