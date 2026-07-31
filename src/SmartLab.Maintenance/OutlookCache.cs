namespace SmartLab.Maintenance;

/// <summary>One cached attachment Outlook wrote when someone opened it.</summary>
public sealed record CachedAttachment(string Path, string Name, long SizeBytes, DateTime LastWritten)
{
    public string SizeText => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F0} KB",
        _ => $"{SizeBytes / 1024.0 / 1024:F1} MB",
    };
}

/// <summary>
/// Outlook's secure temp folder, where opening an attachment leaves a copy.
/// </summary>
/// <remarks>
/// <para>
/// The macOS feature this mirrors clears attachments Mail has downloaded into its own
/// store. Windows has no equivalent store, so this reaches exactly one place: the
/// folder Outlook copies an attachment into so another program can open it. Those
/// copies are never cleaned up, and on a machine that has been in use for years they
/// are frequently the largest forgotten thing in the profile.
/// </para>
/// <para>
/// <b>The mail itself is never touched.</b> An <c>.ost</c> is a cache in Outlook's
/// vocabulary but the mailbox in the user's, and a <c>.pst</c> is often the only copy
/// of mail that no longer exists on any server. Both extensions are refused here
/// rather than filtered at display time, so no future caller can reach them by
/// accident.
/// </para>
/// </remarks>
public static class OutlookCache
{
    /// <summary>
    /// Extensions this feature will never report, whatever folder they turn up in.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedExtensions = [".ost", ".pst", ".nst"];

    /// <summary>
    /// Locates the attachment cache folders.
    /// </summary>
    /// <remarks>
    /// Discovered rather than hardcoded: the leaf folder carries a random suffix that
    /// differs on every machine, and a profile can have more than one.
    /// </remarks>
    public static IReadOnlyList<string> FindCacheFolders()
    {
        var inetCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "INetCache", "Content.Outlook");

        try
        {
            return Directory.Exists(inetCache)
                ? Directory.GetDirectories(inetCache)
                : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>True when this file may be reported and offered for deletion.</summary>
    /// <remarks>
    /// Pure, so the rule that protects a mailbox can be tested without one.
    /// </remarks>
    public static bool IsSafeToOffer(string path)
    {
        var extension = Path.GetExtension(path);

        return !ProtectedExtensions.Any(
            p => string.Equals(p, extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Everything in the cache folders that is safe to offer.</summary>
    public static IReadOnlyList<CachedAttachment> Scan()
    {
        var found = new List<CachedAttachment>();

        foreach (var folder in FindCacheFolders())
        {
            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!IsSafeToOffer(file)) continue;

                try
                {
                    var info = new FileInfo(file);
                    found.Add(new CachedAttachment(file, info.Name, info.Length, info.LastWriteTime));
                }
                catch
                {
                    // A file locked or removed mid-walk is not worth abandoning the
                    // rest of the folder for.
                }
            }
        }

        return found;
    }

    /// <summary>
    /// True when Outlook is running, so the operator can be told why files are locked.
    /// </summary>
    /// <remarks>
    /// An attachment still open in Outlook cannot be deleted. Reporting the skip
    /// without the reason reads as a failure rather than as the machine being in use.
    /// </remarks>
    public static bool IsOutlookRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("OUTLOOK").Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
