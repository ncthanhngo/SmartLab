// Interop structs are filled in by the marshaller, so the compiler cannot see
// that their string fields are assigned. Warnings are disabled for this file
// rather than littering the P/Invoke surface with suppressions — but the
// annotation context stays on, so '?' still carries meaning on the signatures
// below (MoveFileExW genuinely accepts a null destination, for instance).
#nullable disable warnings

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SmartLab.Win32.Native;

internal static class NativeMethods
{
    internal const int MAX_PATH = 260;

    // Error codes seen on the volume that motivated this tool.
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_NO_MORE_FILES = 18;
    internal const int ERROR_NOT_READY = 21;
    internal const int ERROR_INVALID_NAME = 123;
    internal const int ERROR_DIRECTORY_INVALID = 267;
    internal const int ERROR_FILE_CORRUPT = 1392;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public readonly long ToTicks() => ((long)dwHighDateTime << 32) | dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;

        public readonly long FileSize => ((long)nFileSizeHigh << 32) | nFileSizeLow;
    }

    internal enum FINDEX_INFO_LEVELS { Standard = 0, Basic = 1 }

    internal enum FINDEX_SEARCH_OPS { NameMatch = 0, LimitToDirectories = 1 }

    [Flags]
    internal enum MoveFileFlags : uint
    {
        ReplaceExisting = 0x1,

        /// <summary>
        /// Deliberately never used. It permits the OS to satisfy a move by
        /// copying then deleting, which is the silent degradation this tool
        /// exists to avoid — a rename must either be a real directory-entry
        /// update or a visible failure.
        /// </summary>
        CopyAllowed = 0x2,

        WriteThrough = 0x8,
    }

    [Flags]
    internal enum CopyFileFlags : uint
    {
        FailIfExists = 0x1,
        Restartable = 0x2,
        NoBuffering = 0x1000,
    }

    internal const uint PROGRESS_CONTINUE = 0;
    internal const uint PROGRESS_CANCEL = 1;
    internal const uint CALLBACK_CHUNK_FINISHED = 0;

    internal delegate uint CopyProgressRoutine(
        long totalFileSize,
        long totalBytesTransferred,
        long streamSize,
        long streamBytesTransferred,
        uint streamNumber,
        uint callbackReason,
        IntPtr sourceFile,
        IntPtr destinationFile,
        IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFindHandle FindFirstFileExW(
        string fileName,
        FINDEX_INFO_LEVELS infoLevelId,
        out WIN32_FIND_DATAW findFileData,
        FINDEX_SEARCH_OPS searchOp,
        IntPtr searchFilter,
        int additionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextFileW(SafeFindHandle findFile, out WIN32_FIND_DATAW findFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindClose(IntPtr findFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetFileAttributesW(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileAttributesW(string fileName, uint fileAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MoveFileExW(string existingFileName, string? newFileName, MoveFileFlags flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CopyFileExW(
        string existingFileName,
        string newFileName,
        CopyProgressRoutine? progressRoutine,
        IntPtr data,
        ref int cancel,
        CopyFileFlags copyFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateDirectoryW(string pathName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteFileW(string fileName);

    /// <summary>Removes an empty directory. Fails with ERROR_DIR_NOT_EMPTY (145) otherwise.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveDirectoryW(string pathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformationW(
        string rootPathName,
        System.Text.StringBuilder volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        System.Text.StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint GetDriveTypeW(string rootPathName);
}

internal sealed class SafeFindHandle() : SafeHandleMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() => NativeMethods.FindClose(handle);
}
