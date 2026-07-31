using System.IO;
using Microsoft.Win32;

namespace SmartLab.Maintenance;

/// <summary>Splits a full registry path such as <c>HKEY_CURRENT_USER\Software\...</c>.</summary>
internal static class RegistryPath
{
    public static bool TrySplit(string fullPath, out RegistryKey? hive, out string subKey)
    {
        hive = null;
        subKey = string.Empty;

        var slash = fullPath.IndexOf('\\');
        if (slash <= 0) return false;

        var hiveName = fullPath[..slash];
        subKey = fullPath[(slash + 1)..];

        hive = hiveName.ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
            "HKEY_USERS" => Registry.Users,
            _ => null,
        };

        return hive is not null;
    }
}

public sealed class Win32TraceProbe : ITraceProbe
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public long FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>
    /// Recursive size, tolerating unreadable entries.
    /// </summary>
    /// <remarks>
    /// A size that is short because one subfolder was locked is far better than an
    /// exception: the number exists to tell the operator roughly how much they are
    /// about to delete, and a missing number tells them nothing at all.
    /// </remarks>
    public long DirectorySize(string path)
    {
        try
        {
            long total = 0;

            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* vanished or locked mid-walk */ }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    public (long Bytes, int Files) DirectoryStats(string path)
    {
        try
        {
            long bytes = 0;
            var files = 0;

            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch { /* vanished or locked mid-walk */ }
            }

            return (bytes, files);
        }
        catch
        {
            return (0, 0);
        }
    }

    public long RecycleBinSize() => RecycleBin.QuerySize();

    public bool RegistryValueExists(string keyPath, string valueName)
    {
        try
        {
            if (!RegistryPath.TrySplit(keyPath, out var hive, out var subKey)) return false;

            using var key = hive!.OpenSubKey(subKey);
            return key?.GetValue(valueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public bool RegistryKeyExists(string keyPath)
    {
        try
        {
            if (!RegistryPath.TrySplit(keyPath, out var hive, out var subKey)) return false;

            using var key = hive!.OpenSubKey(subKey);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }
}
