namespace UsbDoctor.Maintenance;

/// <summary>
/// Read-only inspection of the machine, so a trace list can be built and tested
/// without touching a real registry or disk.
/// </summary>
public interface ITraceProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);

    /// <summary>Total bytes under a directory. Returns 0 when unreadable.</summary>
    long DirectorySize(string path);

    /// <summary>
    /// Bytes and file count under a directory, in one walk.
    /// </summary>
    /// <remarks>
    /// A cleaner shows both, and walking a cache directory twice to get them
    /// separately doubles the slowest part of a scan.
    /// </remarks>
    (long Bytes, int Files) DirectoryStats(string path);

    long FileSize(string path);

    /// <summary>Bytes currently held in the Recycle Bin across all drives.</summary>
    long RecycleBinSize();

    bool RegistryValueExists(string keyPath, string valueName);
    bool RegistryKeyExists(string keyPath);
}

/// <summary>Performs the deletions. Separate from the probe so a scan cannot delete.</summary>
public interface ITraceRemover
{
    /// <summary>When true, everything is reported but nothing is removed.</summary>
    bool DryRun { get; }

    RemovalResult Remove(AppTrace trace);
}

/// <summary>
/// The locations USB Doctor writes to, injected rather than read from the
/// environment so the scanner is testable and so a caller can point it at a
/// different install.
/// </summary>
public sealed record UninstallPaths(
    string LocalAppData,
    string UserProfile,
    string InstallDirectory)
{
    public const string RunKeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "USB Doctor";

    public static UninstallPaths ForCurrentUser(string installDirectory) => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        installDirectory);
}
